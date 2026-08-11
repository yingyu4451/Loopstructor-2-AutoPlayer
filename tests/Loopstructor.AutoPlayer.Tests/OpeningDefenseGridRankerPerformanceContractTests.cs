using Mono.Cecil;
using Mono.Cecil.Cil;
using Loopstructor.AutoPlayer.Core;

namespace Loopstructor.AutoPlayer.Tests;

public sealed class OpeningDefenseGridRankerPerformanceContractTests
{
    private const string RankerType = "Loopstructor.AutoPlayer.Core.OpeningDefenseGridRanker";
    private const string PlannerType = "Loopstructor.AutoPlayer.Core.OpeningDefensePreparationPlanner";
    private const string LayoutPlannerType = "Loopstructor.AutoPlayer.Core.RailLayoutStrategyPlanner";

    [Fact]
    public void CandidateRankingUsesLightweightGeometryAndDefersFullLoopPlanning()
    {
        using AssemblyDefinition core = ReadCore();
        TypeDefinition ranker = RequireType(core, RankerType);
        MethodReference[] rankingCalls = ranker.Methods
            .Where(method => method.HasBody)
            .SelectMany(Calls)
            .ToArray();

        Assert.DoesNotContain(rankingCalls, call =>
            call.DeclaringType.FullName == LayoutPlannerType &&
            call.Name == "PlanPlayerLoop");
        Assert.Contains(rankingCalls, call =>
            call.DeclaringType.FullName == LayoutPlannerType &&
            call.Name == "EvaluateEstimated");

        MethodDefinition buildRail = RequireType(core, PlannerType).Methods.Single(method =>
            method.Name == "TryBuildRailAction");
        Assert.Contains(Calls(buildRail), call =>
            call.DeclaringType.FullName == LayoutPlannerType &&
            call.Name == "PlanPlayerLoop");
    }

    [Fact]
    public void CandidateRankingIsDeterministicAcrossInputOrder()
    {
        OpeningDefenseGrid[] anchors =
        {
            new(-8, 0),
            new(-4, -4),
            new(0, -8),
            new(4, -4),
            new(8, 0),
            new(4, 4),
            new(0, 8),
            new(-4, 4)
        };
        OpeningDefenseGrid[] candidates = Enumerable.Range(-10, 21)
            .SelectMany(x => Enumerable.Range(-10, 21).Select(y => new OpeningDefenseGrid(x, y)))
            .ToArray();

        IReadOnlyList<OpeningDefenseGrid> forward = OpeningDefenseGridRanker.Rank(candidates, anchors);
        IReadOnlyList<OpeningDefenseGrid> reversed = OpeningDefenseGridRanker.Rank(candidates.Reverse(), anchors.Reverse());

        Assert.Equal(forward, reversed);
    }

    private static AssemblyDefinition ReadCore()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Loopstructor.AutoPlayer.Core.dll");
        Assert.True(File.Exists(path), "Core assembly was not copied to the test output: " + path);
        return AssemblyDefinition.ReadAssembly(path);
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName) =>
        assembly.MainModule.Types.Single(type => type.FullName == fullName);

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) =>
        method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>();
}
