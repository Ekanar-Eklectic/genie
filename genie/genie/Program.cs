using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Linux;

using Microsoft.Extensions.Configuration;

using Tmds.Linux;
using static Tmds.Linux.LibC;

using Process=System.Diagnostics.Process;

namespace ArkaneSystems.WindowsSubsystemForLinux.Genie
{
    public static class Program
    {
        // Install location of genie and friends.
#if LOCAL
        public const string Prefix = "/usr/local";
#else
        public const string Prefix = "/usr";
#endif

        public static IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .SetBasePath ("/etc")
            .AddIniFile ("genie.ini", optional: false)
            .Build ();

        #region System status

        // User ID of the real user running genie.
        public static uid_t realUserId { get; set; }

        // User name of the real user running genie.
        public static string realUserName { get; set;}

        // Group ID of the real user running genie.
        public static gid_t realGroupId { get; set; }

        // PID of the earliest running root systemd, or 0 if there is no running root systemd
        public static int systemdPid { get; set;}

        // Did the bottle exist when genie was started?
        public static bool bottleExistedAtStart { get; set;}

        // Was genie started within the bottle?
        public static bool startedWithinBottle {get; set;}

        // Are we configured to update the hostname for WSL or not?
        public static bool updateHostname {get; set;}

        // Original path before we enforce secure path.
        public static string originalPath {get; set;}

        #endregion System status

        // Entrypoint.
        public static int Main (string[] args)
        {
            // *** PRELAUNCH CHECKS
            // Check that we are, in fact, running on Linux/WSL.
            if (!RuntimeInformation.IsOSPlatform (OSPlatform.Linux))
            {
                Console.WriteLine ("genie: not executing on the Linux platform - how did we get here?");
                return EBADF;
            }

            if (IsWsl1())
            {
                Console.WriteLine ("genie: systemd is not supported under WSL 1.");
                return EPERM;
            }

            if (!IsWsl2())
            {
                Console.WriteLine ("genie: not executing under WSL 2 - how did we get here?");
                return EBADF;
            }

            if (geteuid() != 0)
            {
                Console.WriteLine ("genie: must execute as root - has the setuid bit gone astray?");
                return EPERM;
            }

            // Set up secure path, saving original if specified.
            var securePath = Configuration["genie:secure-path"];

            if (String.Compare(Configuration["genie:clone-path"], "true", true) == 0)
                originalPath = Environment.GetEnvironmentVariable("PATH");
            else
                // TODO: Should reference system drive by letter
                originalPath = @"/mnt/c/Windows/System32";

            Environment.SetEnvironmentVariable ("PATH", securePath);

            // Determine whether or not we will be hostname-updating.
            if (String.Compare(Configuration["genie:update-hostname"], "true", true) == 0)
                updateHostname = true ;
            else
                updateHostname = false ;

            // *** PARSE COMMAND-LINE
            // Create options.
            Option optVerbose = new Option ("--verbose",
                                            "Display verbose progress messages");
            optVerbose.AddAlias ("-v");
            optVerbose.Argument = new Argument<bool>();
            optVerbose.Argument.SetDefaultValue(false);

            // Add them to the root command.
            var rootCommand = new RootCommand();
            rootCommand.Description = "Handles transitions to the \"bottle\" namespace for systemd under WSL.";
            rootCommand.AddOption (optVerbose);
            rootCommand.Handler = CommandHandler.Create<bool>((Func<bool, int>)RootHandler);

            var cmdInitialize = new Command ("--initialize");
            cmdInitialize.AddAlias ("-i");
            cmdInitialize.Description = "Initialize the bottle (if necessary) only.";
            cmdInitialize.Handler = CommandHandler.Create<bool>((Func<bool, int>)InitializeHandler);

            rootCommand.Add (cmdInitialize);

            var cmdShell = new Command ("--shell");
            cmdShell.AddAlias ("-s");
            cmdShell.Description = "Initialize the bottle (if necessary), and run a shell in it.";
            cmdShell.Handler = CommandHandler.Create<bool>((Func<bool, int>)ShellHandler);

            rootCommand.Add (cmdShell);

            var cmdLogin = new Command ("--login");
            cmdLogin.AddAlias ("-l");
            cmdLogin.Description = "Initialize the bottle (if necessary), and open a logon prompt in it.";
            cmdLogin.Handler = CommandHandler.Create<bool>((Func<bool, int>)LoginHandler);

            rootCommand.Add (cmdLogin);

            var argCmdLine = new Argument<IEnumerable<string>> ("command");
            argCmdLine.Description = "The command to execute within the bottle.";
            argCmdLine.Arity = ArgumentArity.OneOrMore;

            var cmdExec = new Command ("--command");
            cmdExec.AddAlias ("-c");
            cmdExec.AddArgument(argCmdLine);
            cmdExec.Description = "Initialize the bottle (if necessary), and run the specified command in it.";
            cmdExec.Handler = CommandHandler.Create<bool, IEnumerable<string>>((Func<bool, IEnumerable<string>, int>)ExecHandler);

            rootCommand.Add (cmdExec);

            var cmdShutdown = new Command ("--shutdown");
            cmdShutdown.AddAlias ("-u");
            cmdShutdown.Description = "Shut down systemd and exit the bottle.";
            cmdShutdown.Handler = CommandHandler.Create<bool>((Func<bool, int>)ShutdownHandler);

            rootCommand.Add (cmdShutdown);

            // Parse the arguments and invoke the handler.
            return rootCommand.InvokeAsync(args).Result;
        }

        // Check if we are being run under WSL 2.
        private static bool IsWsl2()
        {
            if (Directory.Exists("/run/WSL"))
                return true;
            else
            {
                var osrelease = File.ReadAllText("/proc/sys/kernel/osrelease");

                return osrelease.Contains ("microsoft", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Check if we are being run under WSL 1.
        private static bool IsWsl1()
        {
            // We check for WSL 1 by examining the type of the root filesystem. If the
            // root filesystem is lxfs, then we're running under WSL 1.

            var mounts = File.ReadAllLines("/proc/self/mounts");

            foreach (var mnt in mounts)
            {
                var deets = mnt.Split(' ');
                if (deets.Length < 6)
                {
                    Console.WriteLine ("genie: mounts format error; terminating.");
                    Environment.Exit (EBADF);
                }

                if (deets[1] == "/")
                {
                    // Root filesystem.
                    return ((deets[2] == "lxfs") || (deets[2] == "wslfs")) ;
                }
            }

            Console.WriteLine ("genie: cannot find root filesystem mount; terminating.");
            Environment.Exit (EPERM);

            // should never get here
            return true;
        }

        internal static string GetPrefixedPath (string path) => Path.Combine (Prefix, path);

        // Get the pid of the earliest running root systemd, or 0 if none is running.
        private static int GetSystemdPid ()
        {
            var processInfo = ProcessManager.GetProcessInfos (_ => _.ProcessName == "systemd")
                .Where (_ => _.Ruid == 0)
                .OrderBy (_ => _.StartTime)
                .FirstOrDefault();

            return processInfo != null ? processInfo.ProcessId : 0;
        }

        private static int RunAndWait (string command, string args)
        {
            try
            {
                var p = Process.Start (command, args);
                p.WaitForExit();

                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine ($"genie: error executing command '{command} {args}':\r\n{ex.Message}");
                Environment.Exit (127);

                // never reached
                return 127;
            }
        }

        private static void Chain (string command, string args, string onError = "command execution failed;")
        {
            int r = RunAndWait (command, args);

            // Console.WriteLine ($"{command} {args}");

            if (r != 0)
            {
                Console.WriteLine ($"genie: {onError} returned {r}.");
                Environment.Exit (r);
            }
        }

        // Do the work of initializing the bottle.
        private static void InitializeBottle (bool verbose)
        {
            if (verbose)
                Console.WriteLine ("genie: initializing bottle.");

            // Dump the envars
            if (verbose)
                Console.WriteLine ("genie: dumping WSL environment variables.");

            Chain (GetPrefixedPath ("libexec/genie/dumpwslenv.sh"), "",
                   "initializing bottle failed; dumping WSL envars");

            // Create the path file.
            File.WriteAllText("/run/genie.path", originalPath);

            // Now that the WSL hostname can be set via .wslconfig, we're going to make changing
            // it automatically in genie an option, enable/disable in genie.ini. Defaults to on
            // for backwards compatibility.
            if (updateHostname)
            {
                // Generate new hostname.
                if (verbose)
                    Console.WriteLine ("genie: generating new hostname.");

                string externalHost;

                unsafe
                {
                    int success;

                    byte [] bytes = new byte[64] ;
                    fixed (byte * buffer = bytes)
                    {
                        success = gethostname (buffer, 64);
                    }

                    if (success != 0)
                    {
                        Console.WriteLine ($"genie: error retrieving hostname: {success}.");
                        Environment.Exit (success);
                    }

                    externalHost = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                }

                if (verbose)
                    Console.WriteLine ($"genie: external hostname is {externalHost}");

                // Make new hostname.
                string internalHost = $"{externalHost.Substring(0, (externalHost.Length <= 60 ? externalHost.Length : 60))}-wsl";

                File.WriteAllLines ("/run/hostname-wsl", new string[] {
                    internalHost
                });

                unsafe
                {
                    var bytes = Encoding.UTF8.GetBytes("/run/hostname-wsl");
                    fixed (byte* buffer = bytes)
                    {
                        chmod (buffer, Convert.ToUInt16 ("644", 8));
                    }
                }

                // Hosts file: check for old host name; if there, remove it.
                if (verbose)
                    Console.WriteLine ("genie: updating hosts file.");

                try
                {
                    var hosts = File.ReadAllLines ("/etc/hosts");
                    var newHosts = new List<string> (hosts.Length);

                    newHosts.Add ($"127.0.0.1 localhost {internalHost}");

                    foreach (string s in hosts)
                    {
                        if (!(
                            (s.Contains (externalHost) || s.Contains (internalHost))
                             && (s.Contains("127.0.0.1"))
                            ))
                        {
                            newHosts.Add (s);
                        }
                    }

                    File.WriteAllLines ("/etc/hosts", newHosts.ToArray());
                }
                catch (Exception ex)
                {
                    Console.WriteLine ($"genie: error updating host file: {ex.Message}");
                    Environment.Exit (130);
                }

                // Set the new hostname.
                if (verbose)
                    Console.WriteLine ("genie: setting new hostname.");

                Chain ("mount", "--bind /run/hostname-wsl /etc/hostname",
                       "initializing bottle failed; bind mounting hostname");
            }

            // Run systemd in a container.
            if (verbose)
                Console.WriteLine ("genie: starting systemd.");

            Chain ("daemonize", $"{Configuration["genie:unshare"]} -fp --propagation shared --mount-proc systemd",
                   "initializing bottle failed; daemonize");

            // Wait for systemd to be up. (Polling, sigh.)
            do
            {
                Thread.Sleep (500);
                systemdPid = GetSystemdPid();
            } while (systemdPid == 0);
        }

        // Previous UID while rootified.
        private static uid_t previousUid = 0;
        private static gid_t previousGid = 0;

        // Become root.
        private static void Rootify ()
        {
            if (previousUid != 0)
                throw new InvalidOperationException("Cannot rootify root.");

            previousUid = getuid();
            previousGid = getgid();
            setreuid(0, 0);
            setregid(0, 0);

        // Console.WriteLine ($"uid={getuid()} gid={getgid()} euid={geteuid()} egid={getegid()}");
        }

        // Do the work of running a command inside the bottle.
        private static void RunCommand (bool verbose, string commandLine)
        {
            if (verbose)
                Console.WriteLine ($"genie: running command '{commandLine}'");

            Chain ("machinectl",
                   String.Concat ($"shell -q {realUserName}@.host ",
                                  GetPrefixedPath ("libexec/genie/runinwsl.sh"),
                                  $" \"{Environment.CurrentDirectory}\" {commandLine.Trim()}"),
                   "running command failed; machinectl shell");
        }

        // Do the work of starting a shell inside the bottle.
        private static void StartShell (bool verbose)
        {
            if (verbose)
                Console.WriteLine ("genie: starting shell");

            Chain ("machinectl",
                   $"shell -q {realUserName}@.host",
                   "starting shell failed; machinectl shell");
        }

        // Start a user session with a login prompt inside the bottle.
        private static void StartLogin (bool verbose)
        {
            if (verbose)
                Console.WriteLine ("genie: starting login");

            Chain ("machinectl",
                   $"login .host",
                   "starting login failed; machinectl login");
        }

        // Revert from root.
        private static void Unrootify ()
        {
            // if (previousUid == 0)
            //    throw new InvalidOperationException("Cannot unrootify unroot.");

            setreuid(previousUid, previousUid);
            setregid(previousGid, previousGid);
            previousUid = 0;
            previousGid = 0;
        }

        // Update the status of the system for use by the command handlers.
        private static void UpdateStatus (bool verbose)
        {
            // Store the UID and name of the real user.
            realUserId = getuid();
            realUserName = Environment.GetEnvironmentVariable("LOGNAME");

            realGroupId = getgid();

            // Get systemd PID.
            systemdPid = GetSystemdPid();

            // Set startup state flags.
            if (systemdPid == 0)
            {
                bottleExistedAtStart = false;
                startedWithinBottle = false;

                if (verbose)
                    Console.WriteLine ("genie: no bottle present.");
            }
            else if (systemdPid == 1)
            {
                bottleExistedAtStart = true;
                startedWithinBottle = true;

                if (verbose)
                    Console.WriteLine ("genie: inside bottle.");
            }
            else
            {
                bottleExistedAtStart = true;
                startedWithinBottle = false;

                if (verbose)
                    Console.WriteLine ($"genie: outside bottle, systemd pid: {systemdPid}.");
            }
        }

        // Handle the case where genie is invoked without a command specified.
        public static int RootHandler (bool verbose)
        {
            Console.WriteLine("genie: one of the commands -i, -s, or -c must be supplied.");
            return 0;
        }

        // Initialize the bottle (if necessary) only
        public static int InitializeHandler (bool verbose)
        {
            // Update the system status.
            UpdateStatus(verbose);

            // If a bottle exists, we have succeeded already. Exit and report success.
            if (bottleExistedAtStart)
            {
                if (verbose)
                    Console.WriteLine ("genie: bottle already exists (no need to initialize).");

                return 0;
            }

            // Become root - daemonize expects real uid root as well as effective uid root.
            Rootify();

            // Init the bottle.
            InitializeBottle(verbose);

            // Give up root.
            Unrootify();

            return 0;
        }

        public static int ShutdownHandler (bool verbose)
        {
            // Update the system status.
            UpdateStatus(verbose);

            if (!bottleExistedAtStart)
            {
                Console.WriteLine ("genie: no bottle exists.");
                return EINVAL;
            }

            if (startedWithinBottle)
            {
                Console.WriteLine ("genie: cannot shut down bottle from inside bottle; exiting.");
                return EINVAL;
            }

            Rootify();

            if (verbose)
                Console.WriteLine ("genie: running systemctl poweroff within bottle");

            var sd = Process.GetProcessById (systemdPid);

            // Call systemctl to trigger shutdown.
            Chain ("nsenter",
                   String.Concat ($"-t {systemdPid} -m -p systemctl poweroff"),
                   "running command failed; nsenter");

            if (verbose)
                Console.WriteLine ("genie: waiting for systemd to exit");

            // Wait for systemd to exit (maximum 16 s).
            sd.WaitForExit(16000);

            if (updateHostname)
            {
                // Drop the in-bottle hostname.
                if (verbose)
                    Console.WriteLine ("genie: dropping in-bottle hostname");

                Thread.Sleep (500);

                Chain ("umount", "/etc/hostname");
                File.Delete ("/run/hostname-wsl");

                Chain ("hostname", "-F /etc/hostname");
            }

            Unrootify();

            return 0;
        }

        // Initialize the bottle, if necessary, then start a shell in it.
        public static int ShellHandler (bool verbose)
        {
            // Update the system status.
            UpdateStatus(verbose);

            if (startedWithinBottle)
            {
                Console.WriteLine ("genie: already inside the bottle; cannot start shell!");
                return EINVAL;
            }

            Rootify();

            if (!bottleExistedAtStart)
                InitializeBottle(verbose);

            // At this point, we should be outside an existing bottle, one way or another.

            // It shouldn't matter whether we have setuid here, since we start the shell with
            // runuser, which expects root and reassigns uid appropriately.
            StartShell(verbose);

            Unrootify();

            return 0;
        }

        // Initialize the bottle, if necessary, then start a login prompt in it.
        public static int LoginHandler (bool verbose)
        {
            // Update the system status.
            UpdateStatus(verbose);

            if (startedWithinBottle)
            {
                Console.WriteLine ("genie: already inside the bottle; cannot start login prompt!");
                return EINVAL;
            }

            Rootify();

            if (!bottleExistedAtStart)
                InitializeBottle(verbose);

            // At this point, we should be outside an existing bottle, one way or another.

            // It shouldn't matter whether we have setuid here, since we start the shell with
            // runuser, which expects root and reassigns uid appropriately.
            StartLogin(verbose);

            Unrootify();

            return 0;
        }

        // Initialize the bottle, if necessary, then run a command in it.
        public static int ExecHandler (bool verbose, IEnumerable<string> command)
        {
            // Update the system status.
            UpdateStatus(verbose);

            // Recombine command argument.
            StringBuilder cmdLine = new StringBuilder (2048);
            foreach (var s in command.Skip (1))
            {
                cmdLine.Append (s);
                cmdLine.Append (' ');
            }

            if (cmdLine.Length > 0)
                cmdLine.Remove (cmdLine.Length - 1, 1);

            // If already inside, just execute it.
            if (startedWithinBottle)
            {
                var p = Process.Start (command.First(), cmdLine.ToString());
                p.WaitForExit();
                return p.ExitCode;
            }

            Rootify();

            if (!bottleExistedAtStart)
                InitializeBottle(verbose);

            // At this point, we should be inside an existing bottle, one way or another.

            RunCommand (verbose, $"{command.First()} {cmdLine.ToString()}");

            Unrootify();

            return 0;
        }
    }
}
