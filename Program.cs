namespace isolation_demo;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

class User
{
    public string Username { get; set; }
    public string Password { get; set; }
}

class Config
{
    public string Database { get; set; }
    public List<User> Users { get; set; }
}

class ConfigFile
{
    public List<Config> Configs { get; set; }
}
class Step : Dictionary<string, string> { }


class Scenario
{
    public string Name { get; set; }
    public List<Step> Setup { get; set; }
    public List<Step> Steps { get; set; }
}


class ScenariosFile
{
    public List<Scenario> Scenarios { get; set; }
}


class TerminalController
{
    static Process _A, _B;  // ← เก็บไว้ระดับ class
    static List<Process> processes = new List<Process>();

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

    delegate bool ConsoleCtrlDelegate(uint ctrlType);

    static bool OnConsoleClose(uint ctrlType)
    {
        // ctrlType 2 = CTRL_CLOSE_EVENT (กด X)
        // CloseTerminal(_A, _B);
        CloseTerminal(processes);
        return false;
    }
    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_QUICK_EDIT = 0x0040;
    const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll")]
    static extern IntPtr FindWindow(string cls, string title);

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    static void DisableQuickEdit()
    {
        IntPtr consoleHandle = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(consoleHandle, out uint consoleMode);

        // ปิด QuickEdit แต่เปิด Extended flags
        consoleMode &= ~ENABLE_QUICK_EDIT;
        consoleMode |= ENABLE_EXTENDED_FLAGS;

        SetConsoleMode(consoleHandle, consoleMode);
    }

    static Process OpenTerminal(string name, string password, string db, int x, int y, int width, int height)
    {
        string psql = @"C:\Program Files\PostgreSQL\18\bin\psql.exe";
        string color = name == "user1" ? "2" : "3";

        string bat = System.IO.Path.GetTempFileName() + ".bat";
        System.IO.File.WriteAllText(bat,
            $"@echo off\n" +
            $"chcp 65001 > nul\n" +
            $"title {name}\n" +
            $"color {color}\n" +
            $"reg add HKCU\\Console /v QuickEdit /t REG_DWORD /d 0 /f > nul\n" +  // ← เพิ่ม
            $"set PGPASSWORD={password}\n" +
            $"\"{psql}\" -U {name} -d {db} -v \"PROMPT1=[{name}] %%/%%R%%# \"\n"
        );

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"{bat}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });

        Thread.Sleep(1500); // รอหน้าต่างเปิด
        proc.Refresh();

        // จัด position
        MoveWindow(proc.MainWindowHandle, x, y, width, height, true);

        return proc;
    }

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    const uint WM_CHAR = 0x0102;

    static void SendToWindow(Process proc, string sql)
    {
        IntPtr hwnd = proc.MainWindowHandle;

        // ส่งทีละตัวอักษร
        foreach (char c in sql)
        {
            PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            Thread.Sleep(20);
        }
        // ส่ง Enter
        PostMessage(hwnd, WM_CHAR, (IntPtr)'\r', IntPtr.Zero);
    }

    static void Send(Process proc, string sql, string label, string comment = "")
    {
        Console.WriteLine(comment != ""
            ? $"  [{label}] {sql}   -- {comment}"
            : $"  [{label}] {sql}");

        SendToWindow(proc, sql);  // ← ใช้แทน SendKeys
        Thread.Sleep(1000);
    }

    static void Wait(string msg = "")
    {
        Console.WriteLine($"\n  ⏸  {msg}");
        Console.Write("     Press Enter to continue...");
        Console.ReadLine();
    }

    // static void CloseTerminal(Process A, Process B)
    // {
    //     if (A == null || B == null) return;
    //     // แบบ graceful — ส่ง close signal ก่อน
    //     A.CloseMainWindow();
    //     B.CloseMainWindow();

    //     // รอ 2 วินาที ถ้ายังไม่ปิดค่อย kill
    //     if (!A.WaitForExit(2000)) A.Kill();
    //     if (!B.WaitForExit(2000)) B.Kill();
    //     Console.WriteLine("Close terminal...");
    // }
    static void CloseTerminal(List<Process> processes)
    {
        // if (A == null || B == null) return;
        // // แบบ graceful — ส่ง close signal ก่อน
        // A.CloseMainWindow();
        // B.CloseMainWindow();

        // // รอ 2 วินาที ถ้ายังไม่ปิดค่อย kill
        // if (!A.WaitForExit(2000)) A.Kill();
        // if (!B.WaitForExit(2000)) B.Kill();
        // Console.WriteLine("Close terminal...");
        foreach (var process in processes)
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(2000)) process.Kill();
        }
        Console.WriteLine("Close terminal...");
    }
    static ConfigFile LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            string fullPath = Path.GetFullPath(configPath);
            Console.WriteLine($"ERROR: config.yaml not found!");
            Console.WriteLine($"       Expected at: {fullPath}");
            Console.ReadLine();
            return null;
        }
        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var file = deserializer.Deserialize<ConfigFile>(yaml);
        return file;
    }
    // static void RunAll(string yamlPath, Process A, Process B)
    // {
    //     if (!File.Exists(yamlPath))
    //     {
    //         string fullPath = Path.GetFullPath(yamlPath);
    //         Console.WriteLine($"ERROR: scenarios.yaml not found!");
    //         Console.WriteLine($"       Expected at: {fullPath}");
    //         Console.ReadLine();
    //         return;
    //     }
    //     var yaml = File.ReadAllText(yamlPath);
    //     var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    //     var file = deserializer.Deserialize<ScenariosFile>(yaml);

    //     foreach (var scenario in file.Scenarios)
    //     {
    //         Console.WriteLine("\n" + new string('=', 60));
    //         Console.WriteLine($"  {scenario.Name}");
    //         Console.WriteLine(new string('=', 60));

    //         // Setup
    //         Console.WriteLine("\n  [SETUP]");
    //         foreach (var step in scenario.Setup)
    //             foreach (var kv in step)
    //                 if (kv.Key == "user1") Send(A, kv.Value, "user1");

    //         Wait("Setup complete - Start Demo");

    //         // Steps
    //         foreach (var step in scenario.Steps)
    //             foreach (var kv in step)
    //             {
    //                 string comment = step.ContainsKey("comment") ? step["comment"] : "";
    //                 switch (kv.Key)
    //                 {
    //                     case "user1": Send(A, kv.Value, "user1", comment); break;
    //                     case "user2": Send(B, kv.Value, "user2", comment); break;
    //                     case "wait": Wait(kv.Value); break;
    //                 }
    //             }

    //         Wait("End of scenario, Enter to continue...");
    //     }
    // }

    static void RunAll(string yamlPath, List<Process> processes)
    {
        if (!File.Exists(yamlPath))
        {
            string fullPath = Path.GetFullPath(yamlPath);
            Console.WriteLine($"ERROR: scenarios.yaml not found!");
            Console.WriteLine($"       Expected at: {fullPath}");
            Console.ReadLine();
            return;
        }
        if (processes.Count == 0)
        {
            Console.WriteLine($"ERROR: process not create!");
            Console.ReadLine();
            return;
        }
        var yaml = File.ReadAllText(yamlPath);
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        var file = deserializer.Deserialize<ScenariosFile>(yaml);

        foreach (var scenario in file.Scenarios)
        {
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine($"  {scenario.Name}");
            Console.WriteLine(new string('=', 60));

            // Setup
            Console.WriteLine("\n  [SETUP]");
            foreach (var step in scenario.Setup)
                foreach (var kv in step)
                    if (kv.Key == "user1") Send(processes[0], kv.Value, "user1");

            Wait("Setup complete - Start Demo");

            // Steps
            foreach (var step in scenario.Steps)
                foreach (var kv in step)
                {
                    string comment = step.ContainsKey("comment") ? step["comment"] : "";
                    switch (kv.Key)
                    {
                        case "user1": Send(processes[0], kv.Value, "user1", comment); break;
                        case "user2": Send(processes[1], kv.Value, "user2", comment); break;
                        case "user3": Send(processes[2], kv.Value, "user3", comment); break;
                        case "wait": Wait(kv.Value); break;
                    }
                }

            Wait("End of scenario, Enter to continue...");
        }
    }

    static void MoveMainWindow(int x, int y, int w, int h)
    {
        IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
        MoveWindow(hwnd, x, y, w, h, true);
    }

    static void Main(string[] args)
    {
        // default เป็น scenarios.yaml ถ้าไม่ได้ส่ง args
        string yamlPath = args.Length > 0 ? args[0] : "scenarios.yaml";
        Process A = null, B = null;
        try
        {
            AllocConsole();
            DisableQuickEdit();

            // ← เพิ่ม 2 บรรทัดนี้
            SetConsoleCtrlHandler(OnConsoleClose, true);


            var config = LoadConfig("config.yaml");

            int workingWidth = 1920;
            int workingHeight = 1080;
            if (Screen.PrimaryScreen != null)
            {
                workingWidth = Screen.PrimaryScreen.WorkingArea.Width;
                workingHeight = Screen.PrimaryScreen.WorkingArea.Height;
            }
            MoveMainWindow(x: 0, y: workingHeight / 2, w: workingWidth, h: workingHeight / 2);
            if (config != null && config.Configs.Count > 0 && config.Configs[0].Users.Count > 0)
            {
                foreach (var conf in config.Configs)
                {
                    int i = 0;
                    int terminalWidth = workingWidth / config.Configs[0].Users.Count;
                    foreach (var user in conf.Users)
                    {
                        processes.Add(OpenTerminal(user.Username, user.Password, conf.Database, x: i * terminalWidth, y: 0, workingWidth / 2, workingHeight / 2));
                        i++;
                    }
                }
            }
            else
            {

                Console.WriteLine("config.yaml not found! using default config.");
                var processA = OpenTerminal("user1", "user1", "postgres", x: 0, y: 0, workingWidth / 2, workingHeight / 2);
                var processB = OpenTerminal("user2", "user2", "postgres", x: workingWidth / 2, y: 0, workingWidth / 2, workingHeight / 2);
                processes.Add(processA);
                processes.Add(processB);
            }


            // if (config != null && config.Configs.Count > 0 && config.Configs[0].Users.Count >= 2)
            // {
            //     Console.WriteLine("Load config file OK.");
            //     A = OpenTerminal(config.Configs[0].Users[0].Username, config.Configs[0].Users[0].Password, config.Configs[0].Database, x: 0, y: 0);
            //     B = OpenTerminal(config.Configs[0].Users[1].Username, config.Configs[0].Users[1].Password, config.Configs[0].Database, x: 960, y: 0);
            // }
            // else
            // {
            //     Console.WriteLine("config.yaml not found! using default config.");
            //     A = OpenTerminal("user1", "user1", "postgres", x: 0, y: 0);
            //     B = OpenTerminal("user2", "user2", "postgres", x: 960, y: 0);
            // }


            // _A = A;  // ← เก็บไว้ให้ OnConsoleClose ใช้
            // _B = B;

            Console.WriteLine("Load yamlPath = " + yamlPath);
            if (yamlPath != "-1")
            {

                // รัน RunAll บน thread แยก ไม่ถูก block โดย UI
                var thread = new Thread(() =>
                {
                    // RunAll(yamlPath, A, B);
                    RunAll(yamlPath, processes);
                });
                thread.SetApartmentState(ApartmentState.STA); // SendKeys ต้องการ STA
                thread.Start();
                thread.Join();
            }
            else
            {
                Console.WriteLine("Free style mode start!");
            }

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ReadLine(); // ค้างไว้ให้อ่าน error
        }
        finally
        {

            CloseTerminal(processes);
        }
    }
}