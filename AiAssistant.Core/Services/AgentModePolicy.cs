using System.Collections.Generic;
using System.Linq;
using AiAssistant.Core.Models;

namespace AiAssistant.Core.Services;

/// <summary>
/// Canonical agent mode names. Previously these were bare string literals compared in three
/// different layers (UI combo tag, <c>IChatService.ActiveMode</c>, request pipeline), which made
/// a typo or a casing difference silently fall through to the wrong behaviour.
/// </summary>
public static class AgentModes
{
    public const string Plan = "Plan";
    public const string Act = "Act";

    /// <summary>
    /// Maps any input to a known mode. Anything unrecognised (including <c>null</c>) resolves to
    /// <see cref="Plan"/> — the read-only mode — so an unknown value can never accidentally grant
    /// write access.
    /// </summary>
    public static string Normalize(string? mode) =>
        string.Equals(mode, Act, StringComparison.OrdinalIgnoreCase) ? Act : Plan;

    public static bool IsPlan(string? mode) => !IsAct(mode);

    public static bool IsAct(string? mode) => string.Equals(mode, Act, StringComparison.OrdinalIgnoreCase);

    public static string DisplayName(string? mode) => IsAct(mode) ? "Act Mode" : "Plan Mode";
}

/// <summary>
/// Single source of truth for what the agent is allowed to do in each mode, and for the
/// mode-specific instruction appended to the system prompt.
/// </summary>
public static class AgentModePolicy
{
    /// <summary>Name of the tool the model must call to surface a plan for review.</summary>
    public const string PlanToolName = "plan_mode_respond";

    public static readonly HashSet<string> AllowedInPlanMode = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_files",
        "search_codebase",
        "find_usages",
        "get_symbol_references",
        "get_type_hierarchy",
        "list_files",
        "list_workspace_files",
        "get_diagnostics",
        "plan_mode_respond",
        "get_database_schema",
        "check_database_connection",
        "execute_query",
        "execute_command",
        "read_command_output",
        "get_workspace_context",
        "ask_question"
    };

    /// <summary>
    /// Tools whose effect depends on their arguments. These stay available in Plan mode because
    /// they are genuinely useful for investigation, but only behind a read-only guard.
    /// </summary>
    private static readonly HashSet<string> ConditionallyMutatingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_query",
        "execute_command",
    };

    public static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_files",
        "search_codebase",
        "find_usages",
        "get_symbol_references",
        "get_type_hierarchy",
        "list_files",
        "list_workspace_files",
        "get_diagnostics",
        "get_database_schema",
        "check_database_connection",
        "execute_query",
        "execute_command",
        "read_command_output",
        "get_workspace_context",
        "ask_question"
    };

    public static bool IsBlockedInPlanMode(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && !AllowedInPlanMode.Contains(toolName!);

    public static bool NeedsReadOnlyGuardInPlanMode(string? toolName) =>
        !string.IsNullOrEmpty(toolName) && ConditionallyMutatingTools.Contains(toolName!);

    public static bool IsBlockedInStatus(string? toolName, PlanStatus status)
    {
        if (string.IsNullOrEmpty(toolName)) return true;

        switch (status)
        {
            case PlanStatus.Draft:
            case PlanStatus.AwaitingReview:
                return !ReadOnlyTools.Contains(toolName!) && toolName != "propose_plan";
                
            case PlanStatus.RevisionRequested:
                return !ReadOnlyTools.Contains(toolName!) && toolName != "propose_plan" && toolName != "revise_plan";

            case PlanStatus.Approved:
                return !ReadOnlyTools.Contains(toolName!) && toolName != "generate_tasks";

            case PlanStatus.TasksGenerated:
            case PlanStatus.InProgress:
                return false; // All tools allowed in active execution

            case PlanStatus.AwaitingAnswers:
            case PlanStatus.Completed:
                return !ReadOnlyTools.Contains(toolName!); // Only read tools

            default:
                return true;
        }
    }

    /// <summary>
    /// Mode-specific block appended to the system prompt. Kept here rather than inline in the
    /// request pipeline so the wording cannot drift away from the tool gating it describes.
    /// </summary>
    public static string BuildModeInstruction(string? mode)
    {
        if (AgentModes.IsAct(mode))
        {
            return
                "\n\n[MODE: ACT]\n" +
                DatabaseQueryPolicy.ReadOnlyInstruction + "\n" +
                "You have full tool access, including file creation, file edits and command execution.\n" +
                "- If the conversation contains an approved plan, implement it step by step in the order given. " +
                "Reviewer notes attached to a step override the original wording of that step.\n" +
                "- Do not re-propose a plan and do not ask for approval again; the approval already happened.\n" +
                "- Make the edits with your tools rather than printing code for the user to copy.\n" +
                "- After each step, state briefly what you changed. If a step turns out to be impossible or wrong, " +
                "stop, explain why, and ask how to proceed instead of silently improvising.";
        }

        var allowedToolsList = string.Join(", ", AllowedInPlanMode.Where(t => !string.Equals(t, PlanToolName, StringComparison.OrdinalIgnoreCase)));
        return
            "\n\n[MODE: PLAN — READ ONLY]\n" +
            DatabaseQueryPolicy.ReadOnlyInstruction + "\n" +
            $"During plan mode, you may use ONLY these tools to investigate the codebase: {allowedToolsList}. " +
            "All other tools are blocked until the plan is approved. Do not claim to have changed anything.\n" +
            "- Investigate first: read the relevant files and search the codebase until you actually understand " +
            "the change, rather than guessing from file names.\n" +
            "- Before proposing any work, you MUST explain your analysis, understanding of the existing code, and " +
            "intermediate reasoning as regular markdown prose.\n" +
            $"- CRITICAL TRANSITION RULE: If proposing a plan, you MUST call the `{PlanToolName}` tool in the EXACT SAME RESPONSE as your " +
            "analysis prose. Do not output your analysis and then stop. If you stop without calling the tool, the " +
            "planning phase will fail. Call the tool immediately after your prose.\n" +
            $"- EXCEPTION: If the user is only asking a question or requesting analysis, simply answer it in standard text and stop. Do not call `{PlanToolName}`.\n" +
            $"- The `{PlanToolName}` tool must contain a structured list of actionable implementation steps. Each step " +
            "needs a short imperative `title` and a `description` naming the concrete files, types and members involved. " +
            "Analysis and explanations belong in the markdown prose above, not in the plan steps.\n" +
            "- Do NOT emit the plan as markdown prose instead of calling the tool: the approval UI is driven by the " +
            "tool call, and prose alone leaves the user with nothing to approve.\n" +
            "- After calling the tool, stop and wait. The user may approve the plan or send you feedback to revise it.\n" +
            "- If the user sends feedback, treat every note as a binding requirement, revise the whole plan " +
            $"accordingly, and call `{PlanToolName}` again with the complete updated step list.";
    }
}

/// <summary>
/// Mode-specific instructions for the Enhanced Plan & Act Mode system.
/// </summary>
public static class EnhancedAgentModePolicy
{
    public static string BuildInstruction(PlanStatus status)
        => DatabaseQueryPolicy.ReadOnlyInstruction + "\n" + BuildPhaseInstruction(status);

    private static string BuildPhaseInstruction(PlanStatus status)
    {
        switch (status)
        {
            case PlanStatus.Draft:
            case PlanStatus.AwaitingReview:
            case PlanStatus.RevisionRequested:
                return "\n\n[MODE: PLAN — READ ONLY]\n" +
                       "You are in the planning phase. You may read files to investigate the codebase.\n" +
                       "- Analyze the problem thoroughly before acting.\n" +
                       "- For `execute_command`, ensure all shell pipes/redirections are inside the script string quotes (e.g., `powershell -command \"... | ...\"`).\n" +
                       "- When the user asks for a plan, you MUST call the `propose_plan` tool. DO NOT output the plan as standard text.\n" +
                       "- If this is a new plan, call the `propose_plan` tool to submit it for review. The output MUST strictly adhere to the JSON schema.\n" +
                       "- If the user provided feedback, call the `revise_plan` tool to update the existing plan.\n" +
                       "- IMPORTANT: If the user is only asking a question or requesting analysis, simply answer it in standard text and stop. Do not call propose_plan unless an actionable change is requested.\n" +
                       "- You cannot modify files yet. Stop calling tools after proposing or revising the plan, or after answering a question.";
                       
            case PlanStatus.Approved:
                return "\n\n[MODE: ACT — TASK GENERATION]\n" +
                       "The user has approved the plan.\n" +
                       "- You MUST immediately call the `generate_tasks` tool to break the plan down into an execution checklist.\n" +
                       "- The output MUST strictly adhere to the JSON schema. Use specific tool names in `toolsUsed` (e.g., 'replace_file_content').\n" +
                       "- Do not modify any files until tasks are generated. Stop calling tools after generating tasks.";

            case PlanStatus.TasksGenerated:
            case PlanStatus.InProgress:
                return "\n\n[MODE: ACT — EXECUTION]\n" +
                       "You are in execution mode. You have full access to file modification tools.\n" +
                       "- You MUST call `start_task` before beginning work on a task.\n" +
                       "- You MUST call `complete_task` when you have finished all work for that task.\n" +
                       "- Multiple tasks may execute concurrently. You may be called for any task at any time.\n" +
                       "- Only perform the work described in the task you were given. Do not modify files outside your task scope.\n" +
                       "- If you are blocked or need clarification, call `ask_question` and stop calling tools for this task.\n" +
                       "- Once ALL tasks are completed, call `generate_walkthrough` to summarize the changes.";
                       
            case PlanStatus.AwaitingAnswers:
                return "\n\n[MODE: ACT — AWAITING ANSWERS]\n" +
                       "You have asked the user a question and are waiting for an answer.\n" +
                       "- Do NOT make any changes or call tools until the user provides the answer.";

            case PlanStatus.Completed:
                return "\n\n[MODE: COMPLETED]\n" +
                       "The plan is fully executed. Wait for further user instructions.";

            default:
                return "";
        }
    }
}
