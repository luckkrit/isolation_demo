namespace isolation_demo;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ─────────────────────────────────────────────
//  YAML Models
// ─────────────────────────────────────────────

class ProfileUser
{
    public string Username { get; set; }
    public string Password { get; set; }
}

class Profile
{
    public string Type { get; set; }  // "mysql" | "postgres"
    public string Client { get; set; }  // path to mysql.exe / psql.exe
    public string Host { get; set; }
    public string Port { get; set; }
    public string Database { get; set; }
    public string RootUser { get; set; }
    public string RootPassword { get; set; }
    public List<ProfileUser> Users { get; set; }
}

class Step : Dictionary<string, string> { }

class Scenario
{
    public string Name { get; set; }
    public List<string> Profiles { get; set; }  // e.g. ["mysql", "postgres"]
    public List<string> Init { get; set; }  // SQL run as root before steps
    public List<Step> Steps { get; set; }
}

class ScenariosFile
{
    public List<Scenario> Scenarios { get; set; }
}

// ─────────────────────────────────────────────
//  Main Program
// ─────────────────────────────────────────────

class Program
{
    // ── Win32 ────────────────────────────────
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int h2, bool repaint);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    delegate bool ConsoleCtrlDelegate(uint ctrlType);

    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_QUICK_EDIT = 0x0040;
    const uint ENABLE_EXT_FLAGS = 0x0080;
    const uint WM_CHAR = 0x0102;

    // ── State ────────────────────────────────
    static readonly List<Process> _activeTerminals = new();
    static readonly string _profilesDir = "profiles";

    static string PickFile()
    {
        string result = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            using var dlg = new OpenFileDialog
            {
                Title = "Select scenarios.yaml",
                Filter = "YAML files (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                result = dlg.FileName;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    // ── Entry Point ──────────────────────────
    static void Main(string[] args)
    {
        string scenariosPath = args.Length > 0 ? args[0] : "scenarios.yaml";

        // If not found, ask user to pick
        if (!File.Exists(scenariosPath))
            scenariosPath = PickFile();

        if (scenariosPath == null)
        {
            Console.WriteLine("No file selected. Exiting.");
            Console.ReadLine();
            return;
        }
        try
        {
            AllocConsole();
            DisableQuickEdit();
            SetConsoleCtrlHandler(OnClose, true);

            var screen = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            int screenW = screen.Width;
            int screenH = screen.Height;
            int consoleHeight = screenH / 3;

            // Main console sits at the bottom third
            MoveMainWindow(0, screenH - consoleHeight, screenW, consoleHeight);

            RunScenarios(scenariosPath, screenW, screenH - consoleHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            CloseAll(_activeTerminals);
            Console.WriteLine("\nPress Enter to exit...");
            Console.ReadLine();
        }
    }

    // ─────────────────────────────────────────
    //  Scenario Runner
    // ─────────────────────────────────────────

    static void RunScenarios(string scenariosPath, int terminalAreaW, int terminalAreaH)
    {
        if (!File.Exists(scenariosPath))
        {
            Console.WriteLine($"ERROR: {Path.GetFullPath(scenariosPath)} not found.");
            Console.ReadLine();
            return;
        }

        ScenariosFile file;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            file = deserializer.Deserialize<ScenariosFile>(File.ReadAllText(scenariosPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR parsing {scenariosPath}: {ex.Message}");
            Console.ReadLine();
            return;
        }

        foreach (var scenario in file.Scenarios)
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine($"  SCENARIO: {scenario.Name}");
            Console.WriteLine(new string('=', 60));

            foreach (var profileName in scenario.Profiles)
            {
                Console.WriteLine($"\n  ── Profile: {profileName.ToUpper()} ──");

                // ── Load profile ──────────────────────────
                var profile = LoadProfile(profileName);
                if (profile == null) continue;

                // ── Init SQL (DROP/CREATE TABLE, INSERT) ──
                if (scenario.Init != null && scenario.Init.Count > 0)
                {
                    Console.WriteLine("\n  [INIT]");
                    foreach (var sql in scenario.Init)
                        RunRootSql(profile, sql);
                }

                // ── Create users ──────────────────────────
                SetupUsers(profile);

                Wait($"Setup complete — press Enter to start [{profileName.ToUpper()}] demo...");

                // ── Open terminals ────────────────────────
                int userCount = profile.Users.Count;
                int terminalW = terminalAreaW / userCount;
                var processMap = new Dictionary<string, Process>();

                for (int i = 0; i < userCount; i++)
                {
                    var user = profile.Users[i];
                    var proc = OpenTerminal(
                        profile, user,
                        x: i * terminalW, y: 0,
                        w: terminalW, h: terminalAreaH
                    );
                    processMap[user.Username] = proc;
                    _activeTerminals.Add(proc);
                }

                // ── Run steps ─────────────────────────────
                foreach (var step in scenario.Steps)
                {
                    string actor = null;
                    string sql = null;
                    string comment = step.ContainsKey("comment") ? step["comment"] : "";
                    string waitMsg = null;

                    foreach (var kv in step)
                    {
                        if (kv.Key == "actor") actor = kv.Value;
                        else if (kv.Key == "sql") sql = kv.Value;
                        else if (kv.Key == "wait") waitMsg = kv.Value;
                    }

                    if (waitMsg != null)
                    {
                        Wait(waitMsg);
                    }
                    else if (actor != null && sql != null)
                    {
                        if (processMap.ContainsKey(actor))
                            Send(processMap[actor], sql, actor, comment);
                        else
                            Console.WriteLine($"  WARNING: no terminal for actor '{actor}'");
                    }
                }

                Wait($"End of [{profileName.ToUpper()}] — press Enter to close terminals...");

                // ── Close terminals ───────────────────────
                foreach (var proc in processMap.Values)
                {
                    _activeTerminals.Remove(proc);
                    CloseOne(proc);
                }
                Thread.Sleep(800);

                // ── Drop users ────────────────────────────
                TeardownUsers(profile);
            }

            Wait("Scenario complete — press Enter for next scenario...");
        }

        Console.WriteLine("\nAll scenarios finished.");
    }

    // ─────────────────────────────────────────
    //  User Setup / Teardown
    // ─────────────────────────────────────────

    static void SetupUsers(Profile profile)
    {
        Console.WriteLine("\n  [CREATE USERS]");
        foreach (var user in profile.Users)
        {
            if (profile.Type == "mysql")
            {
                RunRootSql(profile, $"DROP USER IF EXISTS '{user.Username}'@'localhost';");
                RunRootSql(profile, $"CREATE USER '{user.Username}'@'localhost' IDENTIFIED BY '{user.Password}';");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON {profile.Database}.* TO '{user.Username}'@'localhost';");
                RunRootSql(profile, "FLUSH PRIVILEGES;");
            }
            else // postgres
            {
                // AFTER
                RunRootSql(profile, $"DO $$ BEGIN IF EXISTS (SELECT FROM pg_roles WHERE rolname = '{user.Username}') THEN REASSIGN OWNED BY {user.Username} TO {profile.RootUser}; DROP OWNED BY {user.Username}; END IF; END $$;");
                RunRootSql(profile, $"DROP USER IF EXISTS {user.Username};");
                RunRootSql(profile, $"CREATE USER {user.Username} WITH PASSWORD '{user.Password}';");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON DATABASE {profile.Database} TO {user.Username};");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {user.Username};");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {user.Username};");  // ← add this
            }
            Console.WriteLine($"    created: {user.Username}");
        }
    }

    static void TeardownUsers(Profile profile)
    {
        Console.WriteLine("\n  [DROP USERS]");
        foreach (var user in profile.Users)
        {
            if (profile.Type == "mysql")
                RunRootSql(profile, $"DROP USER IF EXISTS '{user.Username}'@'localhost';");
            else
            {
                RunRootSql(profile, $"REASSIGN OWNED BY {user.Username} TO {profile.RootUser};");
                RunRootSql(profile, $"DROP OWNED BY {user.Username};");
                RunRootSql(profile, $"DROP USER IF EXISTS {user.Username};");
            }

            Console.WriteLine($"    dropped: {user.Username}");
        }
    }

    // ─────────────────────────────────────────
    //  Root SQL Runner (no terminal, silent)
    // ─────────────────────────────────────────

    static void RunRootSql(Profile profile, string sql)
    {
        Console.WriteLine($"    {sql}");

        string arguments = profile.Type == "mysql"
            ? $"-h {profile.Host} -P {profile.Port} -u {profile.RootUser} -p{profile.RootPassword} -D {profile.Database} -e \"{sql}\""
            : $"-h {profile.Host} -p {profile.Port} -U {profile.RootUser} -d {profile.Database} -c \"{sql}\"";

        var psi = new ProcessStartInfo
        {
            FileName = profile.Client,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (profile.Type == "mysql") psi.Environment["MYSQL_PWD"] = profile.RootPassword;
        else psi.Environment["PGPASSWORD"] = profile.RootPassword;

        using var proc = Process.Start(psi);
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (!string.IsNullOrWhiteSpace(err))
            Console.WriteLine($"    WARN: {err.Trim()}");
    }

    // ─────────────────────────────────────────
    //  Terminal Management
    // ─────────────────────────────────────────

    static Process OpenTerminal(Profile profile, ProfileUser user, int x, int y, int w, int h)
    {
        string script = profile.Type == "mysql"
            ? BuildMySqlScript(profile, user)
            : BuildPsqlScript(profile, user);

        string bat = Path.GetTempFileName() + ".bat";
        File.WriteAllText(bat, script);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"{bat}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });

        Thread.Sleep(1500);
        proc.Refresh();
        MoveWindow(proc.MainWindowHandle, x, y, w, h, true);

        return proc;
    }

    static string BuildMySqlScript(Profile profile, ProfileUser user)
    {
        string color = user.Username == "user1" ? "2" : user.Username == "user2" ? "3" : "4";
        return
            $"@echo off\n" +
            $"chcp 65001 > nul\n" +
            $"title {user.Username} [mysql]\n" +
            $"color {color}\n" +
            $"reg add HKCU\\Console /v QuickEdit /t REG_DWORD /d 0 /f > nul\n" +
            $"set MYSQL_PWD={user.Password}\n" +
            $"\"{profile.Client}\" -h {profile.Host} -P {profile.Port} -u {user.Username} -D {profile.Database} " +
            $"--prompt=\"[{user.Username}] mysql> \"\n";
    }

    static string BuildPsqlScript(Profile profile, ProfileUser user)
    {
        string color = user.Username == "user1" ? "2" : user.Username == "user2" ? "3" : "4";
        string psqlrc = Path.GetTempFileName().Replace("\\", "\\\\");
        return
                $"@echo off\n" +
                $"chcp 65001 > nul\n" +
                $"title {user.Username} [postgres]\n" +
                $"color {color}\n" +
                $"reg add HKCU\\Console /v QuickEdit /t REG_DWORD /d 0 /f > nul\n" +
                $"set PGPASSWORD={user.Password}\n" +
                // Write psqlrc — no cmd escaping needed since it's a file write
                $"echo \\set PROMPT1 '[{user.Username}] %%/%%R%%# ' > \"{psqlrc}\"\n" +
                $"set PSQLRC={psqlrc}\n" +
                $"\"{profile.Client}\" -h {profile.Host} -p {profile.Port} -U {user.Username} -d {profile.Database}\n";
    }

    // ─────────────────────────────────────────
    //  Send / Wait
    // ─────────────────────────────────────────

    static void Send(Process proc, string sql, string label, string comment = "")
    {
        Console.WriteLine(comment != ""
            ? $"  [{label}] {sql}   -- {comment}"
            : $"  [{label}] {sql}");

        IntPtr hwnd = proc.MainWindowHandle;
        foreach (char c in sql)
        {
            PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            Thread.Sleep(20);
        }
        PostMessage(hwnd, WM_CHAR, (IntPtr)'\r', IntPtr.Zero);
        Thread.Sleep(1000);
    }

    static void Wait(string msg = "")
    {
        Console.WriteLine($"\n  ⏸  {msg}");
        Console.Write("     Press Enter to continue...");
        Console.ReadLine();
    }

    // ─────────────────────────────────────────
    //  Profile Loader
    // ─────────────────────────────────────────

    static Profile LoadProfile(string name)
    {
        string path = Path.Combine(_profilesDir, $"{name}.yaml");
        if (!File.Exists(path))
        {
            Console.WriteLine($"  ERROR: profile not found: {Path.GetFullPath(path)}");
            return null;
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<Profile>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR parsing {name}.yaml: {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────
    //  Cleanup
    // ─────────────────────────────────────────

    static void CloseOne(Process proc)
    {
        try
        {
            proc.CloseMainWindow();
            if (!proc.WaitForExit(2000)) proc.Kill();
        }
        catch { }
    }

    static void CloseAll(List<Process> list)
    {
        foreach (var p in list) CloseOne(p);
        list.Clear();
    }

    static bool OnClose(uint ctrlType)
    {
        CloseAll(_activeTerminals);
        return false;
    }

    // ─────────────────────────────────────────
    //  Console Helpers
    // ─────────────────────────────────────────

    static void DisableQuickEdit()
    {
        IntPtr h = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(h, out uint mode);
        mode &= ~ENABLE_QUICK_EDIT;
        mode |= ENABLE_EXT_FLAGS;
        SetConsoleMode(h, mode);
    }

    static void MoveMainWindow(int x, int y, int w, int h)
    {
        IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
        MoveWindow(hwnd, x, y, w, h, true);
    }
}