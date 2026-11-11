using System.IO.Pipes;
using System.Text;

namespace UsageApp.State;

/// <summary>
/// The dashboard's line to the tray process: a named pipe, one tab-separated
/// message per connection.
/// </summary>
/// <remarks>
/// <para><c>hello &lt;pid&gt;</c>: a dashboard is open; the tray stops reading
/// transcripts itself and watches that process end.</para>
/// <para><c>tip &lt;agent&gt; &lt;line&gt;</c>: an agent's tooltip line, so the
/// transcripts are read by one process at a time, never both.</para>
/// <para><c>settings</c>: app-settings.json or start at login changed.</para>
/// <para><c>exit</c>: Exit was chosen in the window; the tray goes too.</para>
/// <para>The other direction needs no pipe: the tray starts
/// <c>--dashboard</c> again with a flag, and the single-instance redirect
/// hands it to the open window (see <see cref="Program"/>).</para>
/// <para>The name carries <see cref="Program.InstanceSuffix"/>, so a demo copy
/// on its own data folder never talks to the real tray.</para>
/// </remarks>
public static class TrayLink
{
    private static string Name => "AIUsage.Tray." + Program.InstanceSuffix;

    /// <summary>Listens until <paramref name="stop"/>; <paramref name="handle"/> runs on the listening thread.</summary>
    public static void Serve(Action<string[]> handle, CancellationToken stop) => new Thread(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                if (reader.ReadLine() is { Length: > 0 } line) handle(line.Split('\t'));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that hung up mid-line: wait for the next one.
            }
        }
    })
    { IsBackground = true, Name = "tray-link" }.Start();

    /// <summary>Sends one message to the tray. False when no tray is listening.</summary>
    public static bool Send(params string[] parts)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine(string.Join('\t', parts.Select(p => p.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '))));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
