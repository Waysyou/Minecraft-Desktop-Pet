using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;

// Qoder hook relay: forwards only session/event/tool/workspace metadata.
// Prompts, tool inputs, outputs, transcripts and credentials never leave this process.
internal static class QoderEventRelay
{
    private const string Pipe = "Waysyou.Minecraft.NativeBridge.v2";

    private static string Field(Dictionary<string, object> input, string key, int length)
    {
        object value;
        string raw = input.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        var result = new StringBuilder();
        foreach (char c in raw)
            if (!Char.IsControl(c) && result.Length < length) result.Append(c);
        return result.ToString();
    }

    private static string MapEvent(string name)
    {
        switch (name)
        {
            case "UserPromptSubmit": return "UserPromptSubmit";
            case "PreToolUse": return "PreToolUse";
            case "PostToolUse": return "PostToolUse";
            // Qoder reports failed tools separately; the bridge only needs it to clear the tool count.
            case "PostToolUseFailure": return "PostToolUse";
            case "PermissionRequest": return "PermissionRequest";
            case "Stop": return "Stop";
            // A fatal stop still ends the current task in the bubble.
            case "StopFailure": return "Stop";
            case "SessionEnd": return "SessionEnd";
            default: return null;
        }
    }

    private static void Send(Dictionary<string, object> data)
    {
        using (var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.Out))
        {
            pipe.Connect(250);
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
            {
                writer.WriteLine(new JavaScriptSerializer { MaxJsonLength = 128 * 1024 }.Serialize(data));
                writer.Flush();
            }
        }
    }

    private static void Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--hook") return;
        try
        {
            string raw;
            using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false))
                raw = reader.ReadToEnd();
            if (raw.Length == 0 || raw.Length > 4 * 1024 * 1024) return;

            var input = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                .Deserialize<Dictionary<string, object>>(raw);
            if (input == null) return;

            string source = Field(input, "source", 20);
            if (source.Length == 0) source = "qoder";
            string name = Field(input, "hook_event_name", 40);
            string mapped = MapEvent(name);
            if (mapped == null) return;

            string cwd = Field(input, "cwd", 500).TrimEnd('\\', '/');
            string workspace = cwd.Length == 0 ? "" : Path.GetFileName(cwd);

            Send(new Dictionary<string, object>
            {
                {"kind", "hook"},
                {"session", Field(input, "session_id", 100)},
                {"turn", Field(input, "turn_id", 100)},
                {"event", mapped},
                {"tool", Field(input, "tool_name", 100)},
                {"workspace", workspace},
                {"source", source}
            });
        }
        catch (Exception) { }
        finally
        {
            try { Console.Out.Write("{}"); } catch (Exception) { }
        }
    }
}
