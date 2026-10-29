using System.Text.Json.Nodes;
using Radix.Core.Interpretation;
using Radix.Core.Model;
using Radix.Core.Serialization;
using Radix.Core.Signals;
using Radix.StdLib;
using Xunit;

namespace Radix.Core.Tests;

public sealed class SerializationTests
{
    private static JsonObject Json(CircuitProject project) =>
        JsonNode.Parse(ProjectSerializer.SaveToString(project))!.AsObject();

    [Fact]
    public void SaveLoadSaveIsStable()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        string first = ProjectSerializer.SaveToString(project);
        CircuitProject loaded = ProjectSerializer.LoadFromString(first);
        string second = ProjectSerializer.SaveToString(loaded);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RoundTripPreservesModelData()
    {
        CircuitProject project = ExampleProjects.CreateFullAdderDemo();
        project.Description = "round trip";
        project.Settings.DefaultAdvanceTicks = 42;
        project.Settings.TraceAllNets = false;
        project.Definitions[0].Instances[0].X = 123.5;
        project.Definitions[0].Instances[0].Y = -4.25;
        project.Definitions[0].Nets[0].InitialValue = 1;
        project.Domains.Add(new SignalDomain(
            "quad-priority",
            "Quad priority",
            ["L", "M", "H", "Z"],
            new PriorityDriverResolution([1, 2, 3, 0], 1)));

        string json = ProjectSerializer.SaveToString(project);
        CircuitProject loaded = ProjectSerializer.LoadFromString(json);

        Assert.Equal(project.Name, loaded.Name);
        Assert.Equal("round trip", loaded.Description);
        Assert.Equal(42, loaded.Settings.DefaultAdvanceTicks);
        Assert.False(loaded.Settings.TraceAllNets);
        Assert.Equal(123.5, loaded.Definitions[0].Instances[0].X);
        Assert.Equal(-4.25, loaded.Definitions[0].Instances[0].Y);
        Assert.Equal(1, loaded.Definitions[0].Nets[0].InitialValue);
        Assert.Equal(5, loaded.RequireDefinition("demo.full-adder").InterfacePins.Count);

        SignalDomain quaternary = Assert.Single(loaded.Domains, d => d.Id == "quad-priority");
        var priority = Assert.IsType<PriorityDriverResolution>(quaternary.Drivers);
        Assert.Equal(new[] { 1, 2, 3, 0 }, priority.Priorities.ToArray());
        Assert.Equal(1, priority.ConflictValue);
    }

    [Fact]
    public void AllPrimitiveKindsRoundTrip()
    {
        var builder = new ProjectBuilder("primitives");
        var root = builder.Define("root", "Root");
        Net a = root.AddPin("a", "A", PinDirection.Input, StandardDomains.BinaryId);
        Net y0 = root.AddPin("y0", "Y0", PinDirection.Output, StandardDomains.BinaryId);
        Net y1 = root.AddPin("y1", "Y1", PinDirection.Output, StandardDomains.BinaryId);
        Net y2 = root.AddPin("y2", "Y2", PinDirection.Output, StandardDomains.BinaryId);
        Net y3 = root.AddPin("y3", "Y3", PinDirection.Output, StandardDomains.BinaryId);

        root.AddPrimitive("lookup", new LookupSpec(1, [1, 0], delayTicks: 2), 10, 20);
        root.AddPrimitive("delay", new DelaySpec(3, initialValue: 1), 30, 40);
        root.AddPrimitive("waveform", new WaveformSpec([2, 3], [0, 1]), 50, 60);
        root.AddPrimitive("constant", new ConstantSpec(1), 70, 80);
        root.AddPrimitive("probe", new ProbeSpec("watch"), 90, 100);

        root.Connect(a, root.Pin("lookup", "in0"), root.Pin("delay", "in0"));
        root.Connect(y0, root.Pin("lookup", "out"), root.Pin("probe", "in0"));
        root.Connect(y1, root.Pin("waveform", "out"));
        root.Connect(y2, root.Pin("delay", "out"));
        root.Connect(y3, root.Pin("constant", "out"));

        string json = ProjectSerializer.SaveToString(builder.Build("root"));
        CircuitProject loaded = ProjectSerializer.LoadFromString(json);
        CircuitDefinition definition = loaded.RequireDefinition("root");

        var lookup = Assert.IsType<LookupSpec>(definition.FindInstance("lookup")!.Primitive);
        Assert.Equal(1, lookup.InputCount);
        Assert.Equal(new[] { 1, 0 }, lookup.Table.ToArray());
        Assert.Equal(2, lookup.DelayTicks);

        var delay = Assert.IsType<DelaySpec>(definition.FindInstance("delay")!.Primitive);
        Assert.Equal(3, delay.DelayTicks);
        Assert.Equal(1, delay.InitialValue);

        var waveform = Assert.IsType<WaveformSpec>(definition.FindInstance("waveform")!.Primitive);
        Assert.Equal(new[] { 2L, 3L }, waveform.Durations.ToArray());
        Assert.Equal(new[] { 0, 1 }, waveform.Values.ToArray());

        Assert.Equal(1, Assert.IsType<ConstantSpec>(definition.FindInstance("constant")!.Primitive).Value);
        Assert.Equal("watch", Assert.IsType<ProbeSpec>(definition.FindInstance("probe")!.Primitive).Label);
    }

    [Fact]
    public void UnsupportedVersionIsRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json["version"] = 99;
        ProjectFormatException error = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingVersionIsRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json.Remove("version");
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
    }

    [Fact]
    public void WrongFormatIsRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json["format"] = "something-else";
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
    }

    [Fact]
    public void MalformedJsonIsRejected()
    {
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString("{ this is not json"));
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json["unexpected"] = true;
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
    }

    [Fact]
    public void UnknownPrimitiveKindIsRejectedWithoutExecutingCode()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        JsonObject definition = json["definitions"]!.AsArray()[0]!.AsObject();
        JsonObject instance = definition["instances"]!.AsArray()[0]!.AsObject();
        JsonObject primitive = instance["primitive"]!.AsObject();
        primitive["kind"] = "arbitrary-code";
        ProjectFormatException error = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
        Assert.Contains("arbitrary-code", error.Message);
    }

    [Fact]
    public void MissingRootIsRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json.Remove("root");
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
    }

    [Fact]
    public void InstanceWithDefinitionAndPrimitiveIsRejected()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        JsonObject definition = json["definitions"]!.AsArray()[0]!.AsObject();
        JsonObject instance = definition["instances"]!.AsArray()[0]!.AsObject();
        instance["definition"] = "main";
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.LoadFromString(json.ToJsonString()));
    }

    [Fact]
    public void SemanticErrorsFailStrictLoadButCanBeInspectedLeniently()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        JsonObject definition = json["definitions"]!.AsArray()[0]!.AsObject();
        JsonObject net = definition["nets"]!.AsArray()[0]!.AsObject();
        JsonObject pinRef = net["pins"]!.AsArray()[0]!.AsObject();
        pinRef["pin"] = "does-not-exist";
        string text = json.ToJsonString();

        Assert.Throws<CircuitValidationException>(() => ProjectSerializer.LoadFromString(text));
        CircuitProject lenient = ProjectSerializer.LoadFromString(text, validate: false);
        Assert.Single(lenient.Definitions);
    }

    [Fact]
    public void SaveIsAtomicAndLeavesNoTemporaryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "radix-serialization-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "project.radix");
            ProjectSerializer.Save(path, TestCircuits.ValidProject());
            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(directory));
            CircuitProject loaded = ProjectSerializer.Load(path);
            Assert.Equal("valid", loaded.Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void JsonUsesDocumentedFormatName()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        Assert.Equal("radix-project", (string?)json["format"]);
        Assert.Equal(ProjectSerializer.CurrentVersion, (int?)json["version"]);
        Assert.Equal("main", (string?)json["root"]);
    }

    [Fact]
    public void ViewMetadataRoundTripsAndVersionFourFilesHaveNoView()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.View.ActiveDefinitionId = "main";
        project.View.Scale = 0.75;
        project.View.OffsetX = 132.5;
        project.View.OffsetY = -48;

        CircuitProject loaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));
        Assert.Equal("main", loaded.View.ActiveDefinitionId);
        Assert.Equal(0.75, loaded.View.Scale);
        Assert.Equal(132.5, loaded.View.OffsetX);
        Assert.Equal(-48, loaded.View.OffsetY);
        Assert.True(loaded.View.HasViewport);

        JsonObject old = Json(TestCircuits.ValidProject());
        old["version"] = 4;
        old.Remove("view");
        CircuitProject oldLoaded = ProjectSerializer.LoadFromString(old.ToJsonString());
        Assert.False(oldLoaded.View.HasViewport);
        Assert.Null(oldLoaded.View.ActiveDefinitionId);
        Assert.Equal(ProjectSerializer.CurrentVersion, (int?)Json(oldLoaded)["version"]);
    }

    [Fact]
    public void WaveformPinsRoundTripAndSanitize()
    {
        CircuitProject project = TestCircuits.ValidProject();
        string netId = project.Definitions[0].Nets[0].Id;
        project.View.WaveformPins["main"] = [netId, netId, " "];
        project.View.WaveformPins["empty"] = [];

        string json = ProjectSerializer.SaveToString(project);
        CircuitProject loaded = ProjectSerializer.LoadFromString(json);

        Assert.Equal([netId], loaded.View.WaveformPins["main"]);
        Assert.False(loaded.View.WaveformPins.ContainsKey("empty"));
        Assert.DoesNotContain("empty", json, StringComparison.Ordinal);

        JsonObject old = Json(TestCircuits.ValidProject());
        old["version"] = 5;
        old.Remove("view");
        CircuitProject oldLoaded = ProjectSerializer.LoadFromString(old.ToJsonString());
        Assert.Empty(oldLoaded.View.WaveformPins);
    }

    [Fact]
    public void VersionOneFilesStillLoad()
    {
        JsonObject json = Json(TestCircuits.ValidProject());
        json["version"] = 1;

        CircuitProject loaded = ProjectSerializer.LoadFromString(json.ToJsonString());

        Assert.Equal(1, loaded.FormatVersion);
        Assert.Single(loaded.Definitions);
    }

    [Fact]
    public void WireColorAndRoutePointsRoundTrip()
    {
        CircuitProject project = ExampleProjects.CreateFullAdderDemo();
        Net net = project.Definitions[0].Nets[0];
        net.WireColor = "#AB12EF";
        net.RoutePoints.Add(new NetWaypoint(120.5, 44));
        net.RoutePoints.Add(new NetWaypoint(-8, 90.25));

        CircuitProject loaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));
        Net reloaded = loaded.Definitions[0].Nets[0];

        Assert.Equal("#AB12EF", reloaded.WireColor);
        Assert.Equal([new NetWaypoint(120.5, 44), new NetWaypoint(-8, 90.25)], reloaded.RoutePoints);
    }

    [Fact]
    public void NumericLanesRoundTripAndVersionTwoFilesRemainReadable()
    {
        CircuitProject project = ExampleProjects.CreateCounterDemo();
        NumericLane lane = Assert.Single(project.RequireDefinition("demo.counter").NumericLanes);
        Assert.Equal(NumericFormats.UnsignedBinary.Id, lane.FormatId);
        Assert.Equal(["q0", "q1", "q2", "q3"], lane.PinIds);

        CircuitProject loaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));
        NumericLane reloaded = Assert.Single(loaded.RequireDefinition("demo.counter").NumericLanes);
        Assert.Equal("Count", reloaded.Name);
        Assert.Equal("unsigned-binary", reloaded.FormatId);
        Assert.Equal(["q0", "q1", "q2", "q3"], reloaded.PinIds);

        JsonObject old = Json(TestCircuits.ValidProject());
        old["version"] = 3;
        CircuitProject oldLoaded = ProjectSerializer.LoadFromString(old.ToJsonString());
        Assert.Empty(oldLoaded.Definitions[0].NumericLanes);
        Assert.Empty(oldLoaded.CustomNumericFormats);
        Assert.Equal(ProjectSerializer.CurrentVersion, (int?)Json(oldLoaded)["version"]);
    }

    [Fact]
    public void CustomNumericFormatsRoundTripAndResolveForLanes()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Domains.Add(new SignalDomain(
            "quinary",
            "Quinary",
            ["0", "1", "2", "3", "4"],
            new SingleDriverResolution()));
        project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "quinary-weighted",
            "Quinary weighted",
            "quinary",
            [0, 1, 2, 3, 4],
            5,
            description: "Custom test format."));
        project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "custom-twos",
            "Custom two's complement",
            StandardDomains.BinaryId,
            [0, 1],
            2,
            WeightingMode.RadixComplement));
        project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "custom-sign",
            "Custom sign-magnitude",
            StandardDomains.BinaryId,
            [0, 1],
            2,
            negativeSignValue: 1));

        CircuitProject loaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));

        Assert.Equal(3, loaded.CustomNumericFormats.Count);
        NumericFormatDefinition quinary = loaded.CustomNumericFormats.Single(format => format.Id == "quinary-weighted");
        Assert.Equal([0L, 1L, 2L, 3L, 4L], quinary.DigitValues);
        Assert.Equal(5, quinary.Radix);
        Assert.Equal("Custom test format.", quinary.Description);
        INumericFormat resolved = Assert.IsType<PositionalFormat>(NumericFormatCatalog.Find(loaded, "custom-twos"));
        Assert.Equal(WeightingMode.RadixComplement, ((PositionalFormat)resolved).Weighting);
        Assert.Equal(-8, (int)resolved.MinimumValue(4));

        NumericFormatDefinition sign = loaded.CustomNumericFormats.Single(format => format.Id == "custom-sign");
        Assert.Equal(1, sign.NegativeSignValue);
        Assert.IsType<SignMagnitudeFormat>(sign.ToFormat());
        Assert.Equal(-7, (int)sign.ToFormat().MinimumValue(4));
    }

    [Fact]
    public void PendingEventLimitRoundTripsWithProjectSettings()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.Settings.MaxPendingEvents = 12345;

        CircuitProject loaded = ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project));

        Assert.Equal(12345, loaded.Settings.MaxPendingEvents);
    }

    [Fact]
    public void CustomNumericFormatValidationRejectsReservedAndMissingDomainIds()
    {
        CircuitProject project = TestCircuits.ValidProject();
        project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "unsigned-binary",
            "Shadow",
            StandardDomains.BinaryId,
            [0, 1],
            2));
        project.CustomNumericFormats.Add(new NumericFormatDefinition(
            "missing-domain",
            "Missing domain",
            "not-a-domain",
            [0, 1],
            2));

        DiagnosticBag report = CircuitValidator.Validate(project);

        Assert.Contains(report, diagnostic => diagnostic.Code == "RAD103");
        Assert.Contains(report, diagnostic => diagnostic.Code == "RAD105");
    }

    [Fact]
    public void NumericLaneValidationRejectsUnknownFormatsAndPinErrors()
    {
        CircuitProject project = TestCircuits.ValidProject();
        CircuitDefinition definition = project.Definitions[0];
        definition.NumericLanes.Add(new NumericLane
        {
            Id = "bad",
            Name = "Bad",
            FormatId = "no-such-format",
        });
        definition.NumericLanes[0].PinIds.AddRange(["a", "a", "missing"]);

        DiagnosticBag report = CircuitValidator.Validate(project);

        Assert.Contains(report, diagnostic => diagnostic.Code == "RAD097");
        Assert.Contains(report, diagnostic => diagnostic.Code == "RAD099");
        Assert.Contains(report, diagnostic => diagnostic.Code == "RAD100");
        Assert.Throws<CircuitValidationException>(() =>
            ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project)));
    }

    [Fact]
    public void InvalidWireColorAndRouteLimitAreRejected()
    {
        CircuitProject project = TestCircuits.ValidProject();
        Net net = project.Definitions[0].Nets[0];
        net.WireColor = "bright green";
        Assert.Throws<ProjectFormatException>(() =>
            ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project)));

        project = TestCircuits.ValidProject();
        net = project.Definitions[0].Nets[0];
        for (int i = 0; i <= WireAnnotation.MaxRoutePoints; i++)
        {
            net.RoutePoints.Add(new NetWaypoint(i, i));
        }

        Assert.Throws<ProjectFormatException>(() =>
            ProjectSerializer.LoadFromString(ProjectSerializer.SaveToString(project)));
    }
}
