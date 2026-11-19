using System.Text.RegularExpressions;

namespace McpCli;

public sealed record CliArguments(string Command, string? Server = null, string? Tool = null,
    string? Pattern = null, string? Json = null, bool WithDescriptions = false, string? ConfigPath = null,
    string? ApprovalId = null, string? ApprovalAction = null)
{
    public static CliArguments Parse(string[] args)
    {
        var descriptions = false;
        string? config = null;
        List<string> positional = [];
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": return new("help");
                case "-v" or "--version": return new("version");
                case "-d" or "--with-descriptions": descriptions = true; break;
                case "-c" or "--config":
                    if (++i == args.Length || args[i].Length == 0) throw CliError.Missing("-c/--config", "path");
                    config = args[i];
                    break;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-") throw CliError.UnknownOption(args[i]);
                    positional.Add(args[i]);
                    break;
            }
        }
        CliArguments Result(string command, string? server = null, string? tool = null,
            string? pattern = null, string? json = null) => new(command, server, tool, pattern, json, descriptions, config);
        if (positional.Count == 0) return Result("list");
        var first = positional[0];
        if (first == "approve")
        {
            if (positional.Count < 5) throw CliError.Missing("approve", "server, tool, approval ID, and action");
            if (positional.Count > 6)
                throw new CliError(1, "TOO_MANY_ARGUMENTS", "Too many arguments for approve");
            if (positional[4] is not ("accept" or "decline" or "cancel"))
                throw new CliError(1, "INVALID_APPROVAL_ACTION", "Approval action must be accept, decline, or cancel");
            if (!Guid.TryParseExact(positional[3], "N", out _))
                throw new CliError(1, "INVALID_APPROVAL_ID", "Approval ID must be the exact ID returned by the pending call");
            return new("approve", positional[1], positional[2], Json: positional.ElementAtOrDefault(5),
                ConfigPath: config, ApprovalId: positional[3], ApprovalAction: positional[4]);
        }
        if (first == "grep")
        {
            if (positional.Count < 2 || positional[1].Length == 0) throw CliError.Missing("grep", "pattern");
            if (positional.Count > 2)
                throw new CliError(1, "TOO_MANY_ARGUMENTS", "Too many arguments for grep",
                    $"Received {positional.Count - 1} arguments, maximum is 1", "Run 'mcp-cli --help' for correct usage");
            return Result("grep", pattern: positional[1]);
        }
        if (first is "info" or "call")
        {
            if (positional.Count == 1)
            {
                if (first == "call") throw CliError.Missing("call", "server and tool");
                return Result("info");
            }
            var target = positional[1];
            var slash = target.IndexOf('/');
            var server = slash < 0 ? target : target[..slash];
            var tool = slash < 0 ? positional.ElementAtOrDefault(2) : target[(slash + 1)..];
            if (first == "info") return Result("info", server, string.IsNullOrEmpty(tool) ? null : tool);
            if (string.IsNullOrEmpty(tool)) throw CliError.Missing("call", "tool");
            var json = string.Join(" ", positional.Skip(slash < 0 ? 3 : 2));
            return Result("call", server, tool, json: json is "" or "-" ? null : json);
        }
        if (CliError.SubcommandAlias(first) is { } alias)
            throw new CliError(1, "UNKNOWN_SUBCOMMAND", $"Unknown subcommand: \"{first}\"",
                "Valid subcommands: info, grep, call", $"Did you mean 'mcp-cli {alias}'?");
        if (first.Contains('/'))
        {
            var parts = first.Split('/');
            throw CliError.Ambiguous(parts[0], parts[1], positional.Count > 1);
        }
        if (positional.Count >= 2 && Regex.IsMatch(positional[1], @"^[a-zA-Z_][a-zA-Z0-9_-]*$"))
            throw CliError.Ambiguous(first, positional[1], positional.Count > 2);
        return Result("info", first);
    }
}
