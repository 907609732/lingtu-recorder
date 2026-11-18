using System.CommandLine;
using System.Text.Json;
using UnpackVision.Agent.Protocol;

namespace UnpackVision.Agent;

public static class AgentCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = BuildRootCommand();
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            WriteError(
                AgentErrorCodes.InvalidArgument,
                "Invalid command line arguments. Run --help for usage.");
            return AgentExitCodes.UsageOrValidation;
        }

        return await parseResult.InvokeAsync();
    }

    public static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "UnpackVision local agent CLI. Business commands print one JSON object on stdout; " +
            "MCP uses stdio and never exposes a remote listener.");
        var operations = new AgentToolOperations(new AgentApiClient());

        root.Subcommands.Add(CreateStatusCommand(operations));
        root.Subcommands.Add(CreateListRecordsCommand(operations));
        root.Subcommands.Add(CreateGetRecordCommand(operations));
        root.Subcommands.Add(CreateListTagsCommand(operations));
        root.Subcommands.Add(CreateSetNoteCommand(operations));
        root.Subcommands.Add(CreateAddTagCommand(operations));
        root.Subcommands.Add(CreateRemoveTagCommand(operations));
        root.Subcommands.Add(CreateRetryCommand(operations));
        root.Subcommands.Add(CreateSchemaCommand());
        root.Subcommands.Add(CreateMcpCommand());
        return root;
    }

    private static Command CreateStatusCommand(AgentToolOperations operations)
    {
        var command = new Command("get-status", "Return desktop readiness and sync semantics.");
        command.SetAction(async (_, cancellationToken) =>
            await ExecuteAsync(AgentToolNames.GetStatus, () => operations.GetStatusAsync(cancellationToken)));
        return command;
    }

    private static Command CreateListRecordsCommand(AgentToolOperations operations)
    {
        var trackingNo = new Option<string>("--tracking-no")
        {
            Description = "Optional tracking-number contains filter."
        };
        var limit = new Option<int?>("--limit")
        {
            Description = $"Maximum rows, 1..{AgentApiContract.MaxListLimit}. Default {AgentApiContract.DefaultListLimit}."
        };
        var command = new Command("list-records", "List recent records with a bounded result.");
        command.Options.Add(trackingNo);
        command.Options.Add(limit);
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.ListRecords,
                () => operations.ListRecordsAsync(
                    parseResult.GetValue(trackingNo),
                    parseResult.GetValue(limit),
                    cancellationToken)));
        return command;
    }

    private static Command CreateGetRecordCommand(AgentToolOperations operations)
    {
        var recordId = RecordIdOption();
        var command = new Command("get-record", "Return one record and its owned media paths.");
        command.Options.Add(recordId);
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.GetRecord,
                () => operations.GetRecordAsync(parseResult.GetValue(recordId), cancellationToken)));
        return command;
    }

    private static Command CreateListTagsCommand(AgentToolOperations operations)
    {
        var command = new Command("list-tags", "List configured issue tags.");
        command.SetAction(async (_, cancellationToken) =>
            await ExecuteAsync(AgentToolNames.ListTags, () => operations.ListTagsAsync(cancellationToken)));
        return command;
    }

    private static Command CreateSetNoteCommand(AgentToolOperations operations)
    {
        var recordId = RecordIdOption();
        var note = new Option<string>("--note")
        {
            Description = $"Note text, {AgentApiContract.MaxNoteLength} characters or fewer. Use an empty string to clear.",
            Required = true
        };
        var command = new Command("set-record-note", "Set a note on a completed/imported record.");
        command.Options.Add(recordId);
        command.Options.Add(note);
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.SetRecordNote,
                () => operations.SetRecordNoteAsync(
                    parseResult.GetValue(recordId),
                    parseResult.GetValue(note),
                    cancellationToken)));
        return command;
    }

    private static Command CreateAddTagCommand(AgentToolOperations operations)
    {
        var recordId = RecordIdOption();
        var tagId = new Option<string>("--tag-id")
        {
            Description = "Configured tag id."
        };
        var command = new Command("add-record-tag", "Add an enabled tag to a completed/imported record.");
        command.Options.Add(recordId);
        command.Options.Add(tagId);
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.AddRecordTag,
                () => operations.AddRecordTagAsync(
                    parseResult.GetValue(recordId),
                    parseResult.GetValue(tagId),
                    cancellationToken)));
        return command;
    }

    private static Command CreateRemoveTagCommand(AgentToolOperations operations)
    {
        var recordId = RecordIdOption();
        var assignmentId = new Option<string>("--assignment-id")
        {
            Description = "Active tag assignment GUID returned by get-record."
        };
        var command = new Command(
            "remove-record-tag",
            "Remove an active tag assignment from a completed/imported record.");
        command.Options.Add(recordId);
        command.Options.Add(assignmentId);
        command.SetAction(async (parseResult, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.RemoveRecordTag,
                () => operations.RemoveRecordTagAsync(
                    parseResult.GetValue(recordId),
                    parseResult.GetValue(assignmentId),
                    cancellationToken)));
        return command;
    }

    private static Command CreateRetryCommand(AgentToolOperations operations)
    {
        var command = new Command(
            "retry-excel-sync",
            "Requeue all Excel deliveries that have not succeeded.");
        command.SetAction(async (_, cancellationToken) =>
            await ExecuteAsync(
                AgentToolNames.RetryExcelSync,
                () => operations.RetryExcelSyncAsync(cancellationToken)));
        return command;
    }

    private static Command CreateSchemaCommand()
    {
        var command = new Command("schema", "Print the offline CLI/MCP schema as JSON.");
        command.SetAction(_ =>
        {
            Console.Out.WriteLine(AgentSchema.CreateJson());
            return 0;
        });
        return command;
    }

    private static Command CreateMcpCommand()
    {
        var command = new Command("mcp", "Run the official MCP server over stdio.");
        command.SetAction(async (_, cancellationToken) =>
        {
            try
            {
                await McpServerHost.RunAsync(cancellationToken);
                return AgentExitCodes.Success;
            }
            catch (OperationCanceledException)
            {
                return AgentExitCodes.Success;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("UnpackVision MCP server could not start.");
                return AgentExitCodes.Unexpected;
            }
        });
        return command;
    }

    private static Option<string> RecordIdOption() => new("--record-id")
    {
        Description = "Record GUID."
    };

    private static async Task<int> ExecuteAsync(string tool, Func<Task<JsonElement>> action)
    {
        try
        {
            var result = await action();
            WriteJson(new
            {
                ok = true,
                tool,
                result
            });
            return AgentExitCodes.Success;
        }
        catch (AgentException exception)
        {
            WriteError(exception.Code, exception.Message);
            return AgentExitCodes.For(exception);
        }
        catch (Exception)
        {
            WriteError(
                AgentErrorCodes.InternalError,
                "The UnpackVision agent command could not complete the request.");
            return AgentExitCodes.Unexpected;
        }
    }

    private static void WriteError(string code, string message) =>
        WriteJson(new
        {
            ok = false,
            error = new { code, message }
        });

    private static void WriteJson(object value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, AgentJson.Options));
}
