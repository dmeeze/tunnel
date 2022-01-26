using System;
using System.CommandLine;
using Renci.SshNet;

namespace Tunnel.Cli;
public class Program
{
    const int RESULT_OK = 0;
    const int RESULT_ERROR = 1;
    static readonly string NL = Environment.NewLine;

    public static async Task<int> Main(params string[] args)
    {
        var remoteOption = new Option<string>(new[] { "--hostname", "-n" },
            description: "The ssh host name to connect to.")
        { IsRequired = true };

        var portOption = new Option<int>("--port",
            description: "The remote ssh port to connect to on the remote host.",
            getDefaultValue: () => 22);

        var userOption = new Option<string>(new[] { "--user", "-u" },
            description: "The ssh user name to connect as.",
            getDefaultValue: () => System.Environment.UserName);

        var passwordOption = new Option<string>(new[] { "--password", "-p" },
            description: "The password for that ssh user (preferred if provided)");

        var keyFileOption = new Option<string>(new[] { "--keyfile", "-k" },
            description: "The private key file for the user.",
            getDefaultValue: () => "~/.ssh/id_rsa");

        var localForwardOption = new Option<string[]>(new[] { "--local", "-l" },
            description: $"Comma separated list of Local ports to forward to Remote [[localip:]localport:][remoteip:]remoteport,... {NL}" +
            $"You can use '*' as your local localip to open the port on all local interfaces." +
            $"Hostnames are supported but think carefully whether the remote host thinks that hostname has the same IP as the local machine." +
            $"Examples: {NL}" +
            $"  --l 80                : *:80           --> 127.0.0.1:80 (equivalent to *:80:127.0.0.1:80){NL}" +
            $"  --l 10.0.0.42:80      : *:80           --> 10.0.0.42:80 (equivalent to *:80:10.0.0.42:80){NL}" +
            $"  --l 8080:10.0.0.42:80 : *:8080         --> 10.0.0.42:80 (equivalent to *:8080:10.0.0.42:80){NL}" +
            $"  --l 192.168.1.1:::80  : 192.168.1.1:80 --> 127.0.0.1:80 (equivalent to 192.168.1.1:80:127.0.0.1:80){NL}")
        { Arity = ArgumentArity.ZeroOrMore };

        var remoteForwardOption = new Option<string[]>(new[] { "--remote", "-r" },
            description: $"Comma separated list of Remote ports to forward to Local [[remoteip:]remoteport:][localip:]localport,... {NL}" +
            $"Hostnames are supported but think carefully whether the remote host thinks that hostname has the same IP as the local machine." +
            $"Examples: {NL}" +
            $"  --l 80                : 127.0.0.1:80   --> 127.0.0.1:80 (equivalent to 127.0.0.1:80:127.0.0.1:80){NL}" +
            $"  --l 10.0.0.42:80      : 127.0.0.1:80   --> 10.0.0.42:80 (equivalent to 127.0.0.1:80:10.0.0.42:80){NL}" +
            $"  --l 8080:10.0.0.42:80 : 127.0.0.1:8080 --> 10.0.0.42:80 (equivalent to 127.0.0.1:8080:10.0.0.42:80){NL}" +
            $"  --l 192.168.1.1:::80  : 192.168.1.1:80 --> 127.0.0.1:80 (equivalent to 192.168.1.1:80:127.0.0.1:80){NL}")
        { Arity = ArgumentArity.ZeroOrMore };

        var cmd = new RootCommand("Connect to a remote system opening a set of tunnelled connections")
        {
            remoteOption,
            portOption,
            userOption,
            passwordOption,
            keyFileOption,
            localForwardOption,
            remoteForwardOption
        };

        cmd.SetHandler<string, int, string, string, string, string[], string[]>(
            sshConnectHandler,
            remoteOption,
            portOption,
            userOption,
            passwordOption,
            keyFileOption,
            localForwardOption,
            remoteForwardOption);

        return await cmd.InvokeAsync(args);
    }

    private static async Task<int> sshConnectHandler(string remote, int port, string user, string password, string keyFile, string[] localForwards, string[] remoteForwards)
    {
        var authMethods = new List<AuthenticationMethod>();

        if (!String.IsNullOrWhiteSpace(password)) authMethods.Add(new PasswordAuthenticationMethod(user, password));
        if (!String.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile)) authMethods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile)));

        if (!authMethods.Any())
        {
            Console.Error.WriteLine("No valid authentication method found. Specify password or key file (and if so, ensure the file exists and is readable)");
            return RESULT_ERROR;
        }

        var forwards = new List<ForwardedPort>();
        try
        {
            forwards.AddRange(parseForwardsLocal(localForwards));
            forwards.AddRange(parseForwardsRemote(remoteForwards));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to parse forwarding rule. Reason: {ex.Message}");
            return RESULT_ERROR;
        }

        var connectionInfo = new ConnectionInfo(remote, port, user, authMethods.ToArray());
        var cancelSource = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, args) =>
        {
            Console.WriteLine("Cancellation requested.");
            args.Cancel = !cancelSource.IsCancellationRequested; // if already requested, let the handler immediately cancel.
            cancelSource.Cancel();
        };

        try
        {
            Console.Write($"Connecting...");
            using var client = new SshClient(connectionInfo);

            client.Connect();
            if (client.IsConnected)
            {
                Console.WriteLine($"Connected.");
                foreach (var forward in forwards)
                {
                    var description = ExplainForward(forward);
                    forward.Exception += (o, e) => Console.Error.WriteLine($"Error on Port Forward [{description}] : {e.Exception.Message}");
                    Console.WriteLine($"Adding Port Forward : {description}");
                    client.AddForwardedPort(forward);
                    forward.Start();
                }
                await waitForEnterAsync(cancelSource);
                foreach (var forward in forwards) try { forward.Stop(); } catch { }
                return RESULT_OK;
            }
            else
            {
                Console.WriteLine($"Failed.");
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Unexpected Error : {ex}");
        }

        return RESULT_ERROR;
    }

    private static Task waitForEnterAsync(CancellationTokenSource cancelSource)
    {

        Console.WriteLine("Press Enter or Esc to exit...");
        return Task.Run(async () =>
        {
            while (!cancelSource.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var cki = Console.ReadKey(true);
                    if (cki.Key == ConsoleKey.Enter || cki.Key == ConsoleKey.Escape) cancelSource.Cancel();
                }
                else
                {
                    await Task.Delay(250, cancelSource.Token);
                }
            }
        });
    }

    private static string ExplainForward(ForwardedPort forward)
    {
        return forward switch
        {
            ForwardedPortLocal l => $"Local {l.BoundHost}:{l.BoundPort} -> Remote {l.Host}:{l.Port}",
            ForwardedPortRemote r => $"Remote {r.BoundHost}:{r.BoundPort} -> Local {r.Host}:{r.Port}",
            _ => $"Unknown Forward"
        };
    }

    private static IEnumerable<ForwardedPortLocal> parseForwardsLocal(IEnumerable<string> forwardArgs)
    {
        string defaultLocal = "*";
        string defaultRemote = "127.0.0.1";
        foreach (var arg in forwardArgs)
        {
            ForwardedPortLocal result;
            try
            {
                var (srcHost, srcPortNum, dstHost, dstPortNum) = parseForwardsInternal(defaultLocal, defaultRemote, arg);
                result = (srcHost == defaultLocal) ? new ForwardedPortLocal(srcPortNum, dstHost, dstPortNum) : new ForwardedPortLocal(srcHost, srcPortNum, dstHost, dstPortNum);
            }
            catch (Exception ex)
            {
                throw new Exception($"Unable to parse '{arg}' as a forwarding rule because {ex.Message}");
            }

            yield return result;
        }
    }

    private static IEnumerable<ForwardedPortRemote> parseForwardsRemote(IEnumerable<string> forwardArgs)
    {
        string defaultLocal = "127.0.0.1";
        string defaultRemote = "127.0.0.1";
        foreach (var arg in forwardArgs)
        {
            ForwardedPortRemote result;
            try
            {
                var (srcHost, srcPortNum, dstHost, dstPortNum) = parseForwardsInternal(defaultLocal, defaultRemote, arg);
                result = new ForwardedPortRemote(srcHost, srcPortNum, dstHost, dstPortNum);
            }
            catch (Exception ex)
            {
                throw new Exception($"Unable to parse '{arg}' as a forwarding rule because {ex.Message}");
            }
            yield return result;
        }
    }

    private static (string? srcHost, uint srcPortNum, string? dstHost, uint dstPortNum) parseForwardsInternal(string defaultSrcHost, string defaultDstHost, string arg)
    {
        string? srcHost;
        string? srcPort;
        uint srcPortNum;
        string? dstHost;
        string? dstPort;
        uint dstPortNum;
        string defaultOnBlank(string value, string defaultValue) => string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        var parts = arg.Split(':', 4, StringSplitOptions.None);
        switch (parts.Length)
        {
            case 1:
                // p
                srcHost = defaultSrcHost;
                srcPort = parts[0];
                dstHost = defaultDstHost;
                dstPort = parts[0];
                break;
            case 2:
                // h:p
                srcHost = defaultSrcHost;
                srcPort = parts[1];
                dstHost = defaultOnBlank(parts[0], defaultDstHost);
                dstPort = parts[1];
                break;
            case 3:
                // p:h:p
                srcHost = defaultSrcHost;
                srcPort = defaultOnBlank(parts[0], parts[2]);
                dstHost = defaultOnBlank(parts[1], defaultDstHost);
                dstPort = parts[2];
                break;
            case 4:
                // h:p:h:p
                srcHost = defaultOnBlank(parts[0], defaultSrcHost);
                srcPort = defaultOnBlank(parts[1], parts[3]);
                dstHost = defaultOnBlank(parts[2], defaultDstHost);
                dstPort = parts[3];
                break;
            default:
                throw new Exception($"Rule has {parts.Length} and must have between 1 and 4 aeparated by colons.");
        }

        if (!uint.TryParse(dstPort, out dstPortNum)) throw new Exception($"Value '{dstPort}' is not a valid port number.");
        if (!uint.TryParse(srcPort, out srcPortNum)) throw new Exception($"Value '{srcPort}' is not a valid port number.");

        return (srcHost, srcPortNum, dstHost, dstPortNum);
    }

}
