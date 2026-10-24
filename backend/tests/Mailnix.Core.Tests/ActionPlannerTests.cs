using Mailnix.Core.Rules;
using static Mailnix.Core.Rules.ConditionField;
using static Mailnix.Core.Rules.ConditionOperator;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class ActionPlannerTests
{
    private static readonly Condition Always = When(Subject, Exists);
    private static readonly Condition Never = When(Subject, NotExists);

    private static MessagePlan Plan(params RuleDefinition[] rules) => ActionPlanner.Plan(rules, Simple(), Context());

    [Fact]
    public void Non_terminal_actions_of_all_matching_rules_are_merged_and_the_move_comes_last()
    {
        var move = Rule(Always, [new MoveToAction("INBOX/Done")], name: "Move", priority: 1);
        var flag = Rule(Always, [new FlagAction(), new AddKeywordAction("seen-by-mailnix")], name: "Flag", priority: 2);
        var read = Rule(Always, [new MarkReadAction()], name: "Read", priority: 3);

        var plan = Plan(read, flag, move);

        plan.Actions.Select(a => a.Type).ShouldBe([ActionType.Flag, ActionType.AddKeyword, ActionType.MarkRead, ActionType.MoveTo]);
        plan.TerminalAction!.TargetFolder.ShouldBe("INBOX.Done");
        plan.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_first_terminal_action_by_priority_applies()
    {
        var archive = Rule(Always, [new ArchiveAction()], name: "Archive", priority: 1);
        var delete = Rule(Always, [new DeleteAction(), new FlagAction()], name: "Delete", priority: 2);

        var plan = Plan(delete, archive);

        plan.TerminalAction.ShouldBe(new PlannedAction(archive.Id, ActionType.Archive, "INBOX.Archive"));
        plan.Actions.ShouldContain(a => a.Type == ActionType.Flag && a.RuleId == delete.Id);
        plan.Skipped.ShouldBe([new SkippedAction(delete.Id, ActionType.Delete, "INBOX.Trash", SkipReason.TerminalActionAlreadyPlanned, archive.Id)]);
    }

    [Fact]
    public void Stop_processing_skips_later_rules()
    {
        var first = Rule(Always, [new FlagAction()], name: "First", priority: 1, stopProcessing: true);
        var second = Rule(Always, [new MarkReadAction()], name: "Second", priority: 2);

        var plan = Plan(first, second);

        plan.Actions.Select(a => a.Type).ShouldBe([ActionType.Flag]);
        plan.Rules.Single(r => r.RuleId == second.Id).Evaluated.ShouldBeFalse();
    }

    [Fact]
    public void Stop_processing_only_applies_when_the_rule_matched()
    {
        var first = Rule(Never, [new FlagAction()], name: "First", priority: 1, stopProcessing: true);
        var second = Rule(Always, [new MarkReadAction()], name: "Second", priority: 2);

        Plan(first, second).Actions.Select(a => a.Type).ShouldBe([ActionType.MarkRead]);
    }

    [Fact]
    public void Contradictions_duplicates_and_no_ops_are_reported()
    {
        var read = Rule(Always, [new MarkReadAction(), new AddKeywordAction("todo")], name: "A", priority: 1);
        var unread = Rule(Always, [new MarkUnreadAction(), new AddKeywordAction("TODO"), new UnflagAction()], name: "B", priority: 2);

        var plan = Plan(read, unread);

        plan.Actions.Select(a => a.Type).ShouldBe([ActionType.MarkRead, ActionType.AddKeyword]);
        plan.Skipped.Select(s => s.Reason).ShouldBe([SkipReason.ContradictsEarlierAction, SkipReason.Duplicate, SkipReason.NoChange]);
    }

    [Fact]
    public void Moving_into_the_current_folder_is_skipped_to_avoid_loops()
    {
        var plan = ActionPlanner.Plan([Rule(Always, [new MoveToAction("INBOX/Done")])], Simple(folder: "INBOX.Done"), Context());

        plan.Actions.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(SkipReason.AlreadyInTargetFolder);
    }

    [Fact]
    public void Special_folder_actions_need_the_folder_to_be_configured()
    {
        var context = Context(c => c with { SpecialFolders = new SpecialFolderTargets(null, null, null) });

        var plan = ActionPlanner.Plan([Rule(Always, [new MarkAsSpamAction()])], Simple(), context);

        plan.Actions.ShouldBeEmpty();
        plan.Skipped.Single().Reason.ShouldBe(SkipReason.SpecialFolderNotConfigured);
    }

    [Fact]
    public void Disabled_rules_do_nothing_and_rules_run_in_priority_order()
    {
        var disabled = Rule(Always, [new DeleteAction()], name: "Disabled", priority: 0, enabled: false);
        var later = Rule(Always, [new MoveToAction("Later")], name: "Later", priority: 5);
        var earlier = Rule(Always, [new MoveToAction("Earlier")], name: "Earlier", priority: 1);

        var plan = Plan(later, disabled, earlier);

        plan.TerminalAction!.TargetFolder.ShouldBe("Earlier");
        plan.Rules.Select(r => r.Name).ShouldBe(["Earlier", "Later"]);
    }

    [Fact]
    public void Dry_run_rules_are_planned_separately_and_do_not_block_live_rules()
    {
        var dryRun = Rule(Always, [new DeleteAction()], name: "Trial", priority: 1, dryRun: true, stopProcessing: true);
        var live = Rule(Always, [new MoveToAction("INBOX/Kept")], name: "Live", priority: 2);

        var plan = Plan(dryRun, live);

        plan.TerminalAction!.TargetFolder.ShouldBe("INBOX.Kept");
        plan.DryRunActions.ShouldBe([new PlannedAction(dryRun.Id, ActionType.Delete, "INBOX.Trash")]);
    }

    [Fact]
    public void Copies_are_deduplicated()
    {
        var a = Rule(Always, [new CopyToAction("Backup/{year}")], name: "A", priority: 1);
        var b = Rule(Always, [new CopyToAction("Backup/{year}")], name: "B", priority: 2);

        var plan = Plan(a, b);

        plan.Actions.ShouldBe([new PlannedAction(a.Id, ActionType.CopyTo, "Backup.2026")]);
        plan.Skipped.Single().Reason.ShouldBe(SkipReason.Duplicate);
    }

    [Fact]
    public void Traces_are_included_on_request()
    {
        var rule = Rule(Always, [new FlagAction()]);

        ActionPlanner.Plan([rule], Simple(), Context(), trace: true).Rules.Single().Trace.ShouldNotBeNull();
        ActionPlanner.Plan([rule], Simple(), Context()).Rules.Single().Trace.ShouldBeNull();
    }
}
