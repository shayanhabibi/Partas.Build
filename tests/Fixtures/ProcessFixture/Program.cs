using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ProcessFixture;

/// <summary>
/// A console child whose output, exit code and lifetime are dictated by its arguments.
/// </summary>
/// <remarks>
/// Every byte is written through a UTF-8 <see cref="StreamWriter"/> over the raw standard stream with the
/// newlines spelled out, so the text a test asserts against is identical on every platform.
/// </remarks>
internal static class Program
{
    private static StreamWriter Open(Stream stream) =>
        new(stream, new UTF8Encoding(false)) { AutoFlush = true };

    private static int Main(string[] args)
    {
        using var stdout = Open(Console.OpenStandardOutput());
        using var stderr = Open(Console.OpenStandardError());

        if (args.Length == 0)
        {
            return 99;
        }

        switch (args[0])
        {
            // text <exitCode>: blank lines on both streams, and a stderr tail without a trailing newline.
            case "text":
                stdout.Write("alpha\n\nbeta\n");
                stderr.Write("err-one\n\nerr-two");
                return int.Parse(args[1]);

            // echo <text>: the text as given, then a newline, on stdout.
            case "echo":
                stdout.Write(args[1] + "\n");
                return 0;

            // args <value>...: one `<length>:<value>` line per argument, so a re-split argument is visible.
            case "args":
                for (var i = 1; i < args.Length; i++)
                {
                    stdout.Write(args[i].Length + ":" + args[i] + "\n");
                }

                return 0;

            // env <name>: the working directory and one environment variable, one per line.
            case "env":
                stdout.Write(Environment.CurrentDirectory + "\n" + Environment.GetEnvironmentVariable(args[1]) + "\n");
                return 0;

            // flood <lines>: that many lines on each stream, alternating, each flushed as it is written.
            case "flood":
                var lines = int.Parse(args[1]);
                var filler = new string('x', 64);
                for (var i = 0; i < lines; i++)
                {
                    stdout.Write("out " + i + " " + filler + "\n");
                    stderr.Write("err " + i + " " + filler + "\n");
                }

                return 0;

            // sleep <milliseconds>: exits long after the test gives up on it.
            case "sleep":
                Thread.Sleep(int.Parse(args[1]));
                return 0;

            default:
                return 99;
        }
    }
}
