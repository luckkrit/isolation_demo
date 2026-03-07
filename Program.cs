namespace isolation_demo;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MySql.Data.MySqlClient;
using Npgsql;
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
    public string Type { get; set; }         // "mysql" | "postgres"
    public string Client { get; set; }       // path to mysql.exe / psql.exe (optional)
    public string Host { get; set; }
    public string Port { get; set; }
    public string Database { get; set; }
    public string RootUser { get; set; }
    public string RootPassword { get; set; }
    public List<ProfileUser> Users { get; set; }

    // ── Auto-detect terminal mode ─────────────
    public bool UseFormTerminal =>
        string.IsNullOrWhiteSpace(Client) || !File.Exists(Client);
}

class Step : Dictionary<string, string> { }

class Scenario
{
    public string Name { get; set; }
    public List<string> Profiles { get; set; }
    public List<string> Init { get; set; }
    public List<Step> Steps { get; set; }
}

class ScenariosFile
{
    public List<Scenario> Scenarios { get; set; }
}

// ─────────────────────────────────────────────
//  DB Terminal Interface
// ─────────────────────────────────────────────
interface IDbTerminal
{
    void Connect();
    void Execute(string sql);
    void Disconnect();
}

// ─────────────────────────────────────────────
//  Base Terminal (shared helpers)
// ─────────────────────────────────────────────
abstract class DbTerminalBase : IDbTerminal
{
    protected readonly Action<string, Color?> _writeLine;

    protected DbTerminalBase(Action<string, Color?> writeLine)
    {
        _writeLine = writeLine;
    }

    public abstract void Connect();
    public abstract void Execute(string sql);
    public abstract void Disconnect();

    protected bool IsNonQuery(string sql)
    {
        var s = sql.TrimStart().ToUpper();
        return s.StartsWith("INSERT") || s.StartsWith("UPDATE") ||
               s.StartsWith("DELETE") || s.StartsWith("CREATE") ||
               s.StartsWith("DROP") || s.StartsWith("ALTER") ||
               s.StartsWith("BEGIN") || s.StartsWith("START") ||
               s.StartsWith("COMMIT") || s.StartsWith("ROLLBACK") ||
               s.StartsWith("GRANT") || s.StartsWith("FLUSH") ||
               s.StartsWith("TRUNCATE") || s.StartsWith("SET");
    }

    protected void PrintResult(IDataReader dr)
    {
        var cols = Enumerable.Range(0, dr.FieldCount)
                             .Select(i => dr.GetName(i)).ToList();
        var rows = new List<string[]>();
        while (dr.Read())
            rows.Add(Enumerable.Range(0, dr.FieldCount)
                               .Select(i => dr[i]?.ToString() ?? "NULL").ToArray());

        if (cols.Count == 0) { _writeLine("  (no columns)", null); return; }

        int[] widths = cols.Select((c, i) =>
            Math.Max(c.Length,
                rows.Count > 0 ? rows.Max(r => r[i].Length) : 0)
        ).ToArray();

        string Sep() =>
            "+" + string.Join("+", widths.Select(w => new string('-', w + 2))) + "+";
        string FormatRow(string[] r) =>
            "| " + string.Join(" | ", r.Select((v, i) => v.PadRight(widths[i]))) + " |";

        _writeLine(Sep(), null);
        _writeLine(FormatRow(cols.ToArray()), null);
        _writeLine(Sep(), null);
        foreach (var row in rows)
            _writeLine(FormatRow(row), null);
        _writeLine(Sep(), null);
        _writeLine($"  ({rows.Count} row{(rows.Count == 1 ? "" : "s")})\n", null);
    }
}

// ─────────────────────────────────────────────
//  PostgreSQL Terminal (Npgsql)
// ─────────────────────────────────────────────
class PgTerminal : DbTerminalBase
{
    private NpgsqlConnection _conn;
    private readonly string _connStr;

    public PgTerminal(string host, string port, string db,
                      string user, string pass,
                      Action<string, Color?> writeLine)
        : base(writeLine)
    {
        _connStr = $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
    }

    public override void Connect()
    {
        _conn = new NpgsqlConnection(_connStr);
        _conn.Open();
    }

    public override void Execute(string sql)
    {
        using var cmd = new NpgsqlCommand(sql, _conn);
        if (IsNonQuery(sql))
        {
            int rows = cmd.ExecuteNonQuery();
            _writeLine($"  OK ({rows} rows affected)\n", null);
            return;
        }
        using var dr = cmd.ExecuteReader();
        PrintResult(dr);
    }

    public override void Disconnect() => _conn?.Close();
}

// ─────────────────────────────────────────────
//  MySQL Terminal (MySql.Data)
// ─────────────────────────────────────────────
class MySqlTerminal : DbTerminalBase
{
    private MySqlConnection _conn;
    private readonly string _connStr;

    public MySqlTerminal(string host, string port, string db,
                         string user, string pass,
                         Action<string, Color?> writeLine)
        : base(writeLine)
    {
        _connStr = $"Server={host};Port={port};Database={db};Uid={user};Pwd={pass};";
    }

    public override void Connect()
    {
        _conn = new MySqlConnection(_connStr);
        _conn.Open();
    }

    public override void Execute(string sql)
    {
        using var cmd = new MySqlCommand(sql, _conn);
        if (IsNonQuery(sql))
        {
            int rows = cmd.ExecuteNonQuery();
            _writeLine($"  OK ({rows} rows affected)\n", null);
            return;
        }
        using var dr = cmd.ExecuteReader();
        PrintResult(dr);
    }

    public override void Disconnect() => _conn?.Close();
}

// ─────────────────────────────────────────────
//  ConsoleForm — WinForms fake terminal
// ─────────────────────────────────────────────
class ConsoleForm : Form
{
    private RichTextBox _console;
    private IDbTerminal _terminal;
    private Color _mainColor;
    private string _prompt;
    private string _inputBuffer = "";
    public static float FontSize = 13f;

    public ConsoleForm(string username, string password,
                       string host, string port,
                       string database, string dbType, int userIndex = 0)
    {
        Text = $"{username} [{dbType}]";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.Sizable;

        // _mainColor = dbType == "mysql" ? Color.Yellow : Color.LimeGreen;
        _mainColor = userIndex switch
        {
            0 => Color.LimeGreen,
            1 => Color.Yellow,
            2 => Color.Cyan,
            _ => Color.White
        };
        _prompt = dbType == "mysql"
            ? $"[{username}] mysql> "
            : $"[{username}] postgres=# ";

        _console = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = _mainColor,
            Font = new Font("Consolas", FontSize),
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            ShortcutsEnabled = false,
        };
        _console.KeyPress += OnKeyPress;
        _console.KeyDown += OnKeyDown;

        Controls.Add(_console);

        Action<string, Color?> output = (text, color) => AppendLine(text, color);

        _terminal = dbType == "mysql"
            ? new MySqlTerminal(host, port, database, username, password, output)
            : new PgTerminal(host, port, database, username, password, output);

        Load += (s, e) => Connect();
    }

    void Connect()
    {
        try
        {
            _terminal.Connect();
            AppendLine($"Connected to database as \"{Text}\".");
            AppendLine(new string('-', 50));
            AppendPrompt();
        }
        catch (Exception ex)
        {
            AppendLine($"ERROR: {ex.Message}", Color.Red);
        }
    }

    void OnKeyPress(object sender, KeyPressEventArgs e)
    {
        e.Handled = true;
        if (e.KeyChar == '\r' || e.KeyChar == '\b') return;

        _inputBuffer += e.KeyChar;
        _console.SelectionColor = _mainColor;
        _console.AppendText(e.KeyChar.ToString());
        _console.ScrollToCaret();
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            string sql = _inputBuffer.Trim();
            _inputBuffer = "";
            _console.AppendText("\n");  // just newline, prompt already visible

            if (!string.IsNullOrWhiteSpace(sql))
                ExecuteSql(sql, echoPrompt: false); // false = don't echo, user already typed it
            else
                AppendPrompt();
        }
        else if (e.KeyCode == Keys.Back)
        {
            e.Handled = true;
            if (_inputBuffer.Length > 0)
            {
                _inputBuffer = _inputBuffer[..^1];
                _console.SelectionStart = _console.TextLength - 1;
                _console.SelectionLength = 1;
                _console.SelectedText = "";
            }
        }
        else if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            e.Handled = true;
        }
    }

    // Called by orchestrator to send SQL programmatically
    public static int TypingDelayMs = 30; // adjust globally

    public void ExecuteSql(string sql, bool echoPrompt = true)
    {
        if (InvokeRequired) { Invoke(() => ExecuteSql(sql, echoPrompt)); return; }

        if (echoPrompt)
        {
            foreach (char c in sql)
            {
                _console.SelectionColor = _mainColor;
                _console.AppendText(c.ToString());
                _console.ScrollToCaret();
                if (TypingDelayMs > 0)
                {
                    Application.DoEvents();
                    Thread.Sleep(TypingDelayMs);
                }
            }
            _console.AppendText("\n");
        }

        try { _terminal.Execute(sql); }
        catch (Exception ex) { AppendLine($"ERROR: {ex.Message}", Color.Red); }

        AppendPrompt();
    }

    public void AppendLine(string text, Color? color = null)
    {
        if (InvokeRequired) { Invoke(() => AppendLine(text, color)); return; }
        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = color ?? _mainColor;
        _console.AppendText(text + "\n");
        _console.ScrollToCaret();
    }

    void AppendPrompt()
    {
        _console.SelectionColor = _mainColor;
        _console.AppendText(_prompt);
        _console.ScrollToCaret();
        _console.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _terminal?.Disconnect();
        base.OnFormClosing(e);
    }
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    delegate bool ConsoleCtrlDelegate(uint ctrlType);

    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_QUICK_EDIT = 0x0040;
    const uint ENABLE_EXT_FLAGS = 0x0080;
    const uint WM_CHAR = 0x0102;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;

    // ── State ────────────────────────────────
    static readonly List<Process> _activeTerminals = new();
    static readonly List<ConsoleForm> _activeForms = new();
    static readonly string _profilesDir = "profiles";

    static SynchronizationContext _uiContext;
    // ─────────────────────────────────────────
    //  Entry Point
    // ─────────────────────────────────────────
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _uiContext = SynchronizationContext.Current
                 ?? new WindowsFormsSynchronizationContext();

        SynchronizationContext.SetSynchronizationContext(_uiContext);


        string scenariosPath = args.Length > 0 ? args[0] : "scenarios.yaml";
        if (!File.Exists(scenariosPath))
            scenariosPath = PickFile();

        if (scenariosPath == null) return;

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
            MessageBox.Show($"ERROR parsing scenarios: {ex.Message}");
            return;
        }

        // ── Allocate orchestrator console ─────
        AllocConsole();
        ReopenConsoleOutput();
        DisableQuickEdit();
        SetConsoleCtrlHandler(OnClose, true);
        Console.OutputEncoding = Encoding.UTF8;

        var screen = Screen.PrimaryScreen?.WorkingArea
                       ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int screenW = screen.Width;
        int screenH = screen.Height;
        int consoleH = screenH / 3;
        MoveMainWindow(0, screenH - consoleH, screenW, consoleH);

        // ── Run scenarios in background ───────
        var bgThread = new Thread(() =>
        {
            try
            {
                RunScenarios(file, screenW, screenH - consoleH);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nERROR: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                CloseAll(_activeTerminals);
                Console.WriteLine("\nPress Enter to exit...");
                Console.ReadLine();
                Application.Exit();
            }
        });
        bgThread.IsBackground = true;
        bgThread.Start();

        Application.Run(); // keeps message loop alive for ConsoleForm windows
    }

    // ─────────────────────────────────────────
    //  Reopen stdout -> CONOUT$
    // ─────────────────────────────────────────
    static void ReopenConsoleOutput()
    {
        IntPtr handle = CreateFile("CONOUT$",
            GENERIC_READ | GENERIC_WRITE, FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

        var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: false);
        var writer = new StreamWriter(new FileStream(safeHandle, FileAccess.Write), Encoding.UTF8)
        { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    // ─────────────────────────────────────────
    //  Scenario Runner
    // ─────────────────────────────────────────
    static void RunScenarios(ScenariosFile file, int terminalAreaW, int terminalAreaH)
    {
        foreach (var scenario in file.Scenarios)
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine($"  SCENARIO: {scenario.Name}");
            Console.WriteLine(new string('=', 60));

            foreach (var profileName in scenario.Profiles)
            {
                Console.WriteLine($"\n  -- Profile: {profileName.ToUpper()} --");

                var profile = LoadProfile(profileName);
                if (profile == null) continue;

                // ── Report terminal mode ──────
                Console.WriteLine(profile.UseFormTerminal
                    ? "  [MODE] Client not found → using Form terminal"
                    : $"  [MODE] Client found → using real terminal ({profile.Client})");

                // ── Init SQL ──────────────────
                if (scenario.Init?.Count > 0)
                {
                    Console.WriteLine("\n  [INIT]");
                    foreach (var sql in scenario.Init)
                        RunRootSql(profile, sql);
                }

                // ── Create users ──────────────
                SetupUsers(profile);
                Wait($"Setup complete — press Enter to start [{profileName.ToUpper()}] demo...");

                // ── Open terminals or forms ───
                int userCount = profile.Users.Count;
                int terminalW = terminalAreaW / userCount;

                var processMap = new Dictionary<string, Process>();
                var formMap = new Dictionary<string, ConsoleForm>();

                for (int i = 0; i < userCount; i++)
                {
                    var user = profile.Users[i];

                    if (profile.UseFormTerminal)
                    {
                        // ── Form terminal ─────
                        var form = OpenConsoleForm(
                            profile, user,
                            x: i * terminalW, y: 0,
                            w: terminalW, h: terminalAreaH, userIndex: i);
                        formMap[user.Username] = form;
                        _activeForms.Add(form);
                    }
                    else
                    {
                        // ── Real terminal ─────
                        var proc = OpenTerminal(
                            profile, user,
                            x: i * terminalW, y: 0,
                            w: terminalW, h: terminalAreaH);
                        processMap[user.Username] = proc;
                        _activeTerminals.Add(proc);
                    }
                }

                // ── Run steps ─────────────────
                foreach (var step in scenario.Steps)
                {
                    string actor = step.ContainsKey("actor") ? step["actor"] : null;
                    string sql = step.ContainsKey("sql") ? step["sql"] : null;
                    string comment = step.ContainsKey("comment") ? step["comment"] : "";
                    string waitMsg = step.ContainsKey("wait") ? step["wait"] : null;

                    if (waitMsg != null)
                    {
                        Wait(waitMsg);
                    }
                    else if (actor != null && sql != null)
                    {
                        Console.WriteLine(comment != ""
                            ? $"  [{actor}] {sql}   -- {comment}"
                            : $"  [{actor}] {sql}");

                        if (profile.UseFormTerminal)
                        {
                            if (formMap.ContainsKey(actor))
                                formMap[actor].ExecuteSql(sql);
                            else
                                Console.WriteLine($"  WARNING: no form for actor '{actor}'");
                        }
                        else
                        {
                            if (processMap.ContainsKey(actor))
                                SendToProcess(processMap[actor], sql);
                            else
                                Console.WriteLine($"  WARNING: no terminal for actor '{actor}'");
                        }

                        Thread.Sleep(500);
                    }
                }

                Wait($"End of [{profileName.ToUpper()}] — press Enter to close terminals...");

                // ── Close terminals/forms ─────
                foreach (var proc in processMap.Values)
                {
                    _activeTerminals.Remove(proc);
                    CloseOne(proc);
                }
                foreach (var form in formMap.Values)
                {
                    _activeForms.Remove(form);
                    form.Invoke(form.Close);
                }

                Thread.Sleep(800);
                TeardownUsers(profile);
            }

            Wait("Scenario complete — press Enter for next scenario...");
        }

        Console.WriteLine("\nAll scenarios finished.");
    }

    // ─────────────────────────────────────────
    //  Open ConsoleForm terminal
    // ─────────────────────────────────────────
    static ConsoleForm OpenConsoleForm(Profile profile, ProfileUser user,
                                        int x, int y, int w, int h, int userIndex = 0)
    {
        ConsoleForm form = null;
        var ready = new ManualResetEventSlim(false);

        // Post to UI thread using captured context
        _uiContext.Post(_ =>
        {
            form = new ConsoleForm(
                user.Username, user.Password,
                profile.Host, profile.Port,
                profile.Database, profile.Type, userIndex);
            form.StartPosition = FormStartPosition.Manual;
            form.Left = x;
            form.Top = y;
            form.Width = w;
            form.Height = h;
            form.Show();
            ready.Set();
        }, null);

        ready.Wait();
        Thread.Sleep(800); // let form connect to DB
        return form;
    }

    // ─────────────────────────────────────────
    //  Open real terminal (cmd.exe + psql/mysql)
    // ─────────────────────────────────────────
    static Process OpenTerminal(Profile profile, ProfileUser user,
                                 int x, int y, int w, int h)
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
        return $"@echo off\nchcp 65001 > nul\ntitle {user.Username} [mysql]\ncolor {color}\n" +
               $"reg add HKCU\\Console /v QuickEdit /t REG_DWORD /d 0 /f > nul\n" +
               $"set MYSQL_PWD={user.Password}\n" +
               $"\"{profile.Client}\" -h {profile.Host} -P {profile.Port} -u {user.Username} " +
               $"-D {profile.Database} --prompt=\"[{user.Username}] mysql> \"\n";
    }

    static string BuildPsqlScript(Profile profile, ProfileUser user)
    {
        string color = user.Username == "user1" ? "2" : user.Username == "user2" ? "3" : "4";
        string psqlrc = Path.GetTempFileName().Replace("\\", "\\\\");
        return $"@echo off\nchcp 65001 > nul\ntitle {user.Username} [postgres]\ncolor {color}\n" +
               $"reg add HKCU\\Console /v QuickEdit /t REG_DWORD /d 0 /f > nul\n" +
               $"set PGPASSWORD={user.Password}\n" +
               $"echo \\set PROMPT1 '[{user.Username}] %%/%%R%%# ' > \"{psqlrc}\"\n" +
               $"set PSQLRC={psqlrc}\n" +
               $"\"{profile.Client}\" -h {profile.Host} -p {profile.Port} " +
               $"-U {user.Username} -d {profile.Database}\n";
    }

    // ─────────────────────────────────────────
    //  Send to real terminal via WM_CHAR
    // ─────────────────────────────────────────
    static void SendToProcess(Process proc, string sql)
    {
        proc.Refresh();
        IntPtr hwnd = proc.MainWindowHandle;
        foreach (char c in sql)
        {
            PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            Thread.Sleep(20);
        }
        PostMessage(hwnd, WM_CHAR, (IntPtr)'\r', IntPtr.Zero);
        Thread.Sleep(1000);
    }

    // ─────────────────────────────────────────
    //  Root SQL Runner
    // ─────────────────────────────────────────
    static void RunRootSql(Profile profile, string sql)
    {
        Console.WriteLine($"    {sql}");
        try
        {
            if (profile.Type == "mysql")
            {
                string cs = $"Server={profile.Host};Port={profile.Port};" +
                            $"Database={profile.Database};Uid={profile.RootUser};Pwd={profile.RootPassword};";
                using var conn = new MySqlConnection(cs);
                conn.Open();
                new MySqlCommand(sql, conn).ExecuteNonQuery();
            }
            else
            {
                string cs = $"Host={profile.Host};Port={profile.Port};" +
                            $"Database={profile.Database};Username={profile.RootUser};Password={profile.RootPassword}";
                using var conn = new NpgsqlConnection(cs);
                conn.Open();
                new NpgsqlCommand(sql, conn).ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Console.WriteLine($"    WARN: {ex.Message.Trim()}"); }
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
            else
            {
                RunRootSql(profile,
                    $"DO $$ BEGIN IF EXISTS (SELECT FROM pg_roles WHERE rolname = '{user.Username}') " +
                    $"THEN REASSIGN OWNED BY {user.Username} TO {profile.RootUser}; " +
                    $"DROP OWNED BY {user.Username}; END IF; END $$;");
                RunRootSql(profile, $"DROP USER IF EXISTS {user.Username};");
                RunRootSql(profile, $"CREATE USER {user.Username} WITH PASSWORD '{user.Password}';");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON DATABASE {profile.Database} TO {user.Username};");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO {user.Username};");
                RunRootSql(profile, $"GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO {user.Username};");
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
    //  Wait / File Picker
    // ─────────────────────────────────────────
    static void Wait(string msg = "")
    {
        Console.WriteLine($"\n  [*]  {msg}");
        Console.Write("     Press Enter to continue...");
        Console.ReadLine();
    }

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
        try { proc.CloseMainWindow(); if (!proc.WaitForExit(2000)) proc.Kill(); } catch { }
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