using Radix.Core.Model;
using Radix.Core.Signals;

namespace Radix.Core.Analysis;

/// <summary>
/// Structural electrical checks over the authored model. These rules deliberately
/// use only information the project actually contains: pin direction, net
/// connectivity, domains, and driver policy. They do not calculate resistance,
/// voltage, current, power, or thermal behavior.
/// </summary>
public static class ElectricalRuleChecker
{
    public const string FloatingInputNetCode = "RAD-ERC-001";
    public const string InputPortDrivenCode = "RAD-ERC-002";

    /// <summary>
    /// Adds high-confidence structural diagnostics. Existing input-pin and
    /// multiple-driver diagnostics (RAD080/RAD081) remain in <see cref="CircuitValidator"/>
    /// so their stable codes and locations are preserved.
    /// </summary>
    public static void Check(CircuitProject project, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (CircuitDefinition definition in project.Definitions)
        {
            var instancesById = new Dictionary<string, ComponentInstance>(
                definition.Instances.Count,
                StringComparer.Ordinal);
            foreach (ComponentInstance instance in definition.Instances)
            {
                instancesById.TryAdd(instance.Id, instance);
            }

            var inputPortNets = new HashSet<string>(StringComparer.Ordinal);
            foreach (PortBinding port in definition.Ports)
            {
                PinDeclaration? pin = definition.FindPin(port.PinId);
                if (pin?.Direction == PinDirection.Input)
                {
                    inputPortNets.Add(port.NetId);
                }
            }

            foreach (Net net in definition.Nets)
            {
                if (project.FindDomain(net.DomainId) is not SignalDomain domain)
                {
                    continue;
                }

                int inputPins = 0;
                int outputPins = 0;
                var inputNames = new List<string>();
                var outputNames = new List<string>();
                foreach (PinRef pinRef in net.Pins)
                {
                    if (!instancesById.TryGetValue(pinRef.InstanceId, out ComponentInstance? instance))
                    {
                        continue;
                    }

                    PinDescriptor? pin = project.FindInstancePin(instance, pinRef.PinId);
                    if (pin is null)
                    {
                        continue;
                    }

                    if (pin.Value.Direction == PinDirection.Input)
                    {
                        inputPins++;
                        inputNames.Add(pinRef.ToString());
                    }
                    else
                    {
                        outputPins++;
                        outputNames.Add(pinRef.ToString());
                    }
                }

                bool hasInputPort = inputPortNets.Contains(net.Id);
                if (inputPins > 0 &&
                    outputPins == 0 &&
                    !hasInputPort &&
                    net.InitialValue is null)
                {
                    diagnostics.AddUnique(new Diagnostic(
                        FloatingInputNetCode,
                        DiagnosticSeverity.Warning,
                        $"Net '{net.Id}' has {inputPins} input pin(s) but no output driver or input port; it will remain undriven. Inputs: {string.Join(", ", inputNames)}.",
                        definition.Id,
                        NetId: net.Id)
                    {
                        Assumption = "Only instance output pins and interface input ports are treated as sources. A floating terminal is not treated as a logic value.",
                        Suggestion = "Connect the net to a driven output or bind a definition input port. If the undriven hold is intentional, set an explicit net initial value and document it.",
                        SimulationRemainsValid = true,
                        Nature = DiagnosticNature.Structural,
                    });
                }

                if (hasInputPort &&
                    outputPins > 0 &&
                    domain.Drivers is SingleDriverResolution)
                {
                    diagnostics.AddUnique(new Diagnostic(
                        InputPortDrivenCode,
                        DiagnosticSeverity.Warning,
                        $"Net '{net.Id}' is bound to an input port and also driven by {outputPins} instance output pin(s) ({string.Join(", ", outputNames)}). The single-driver policy can conflict with the external drive.",
                        definition.Id,
                        NetId: net.Id)
                    {
                        Assumption = "An interface input pin is treated as externally driven by its parent or testbench. Multiple active values on a single-driver net are a structural conflict, not a calculated short-circuit current.",
                        Suggestion = "Remove the internal driver, expose the signal as an output, or use a domain with an explicit multi-driver policy if shared drive is intentional.",
                        SimulationRemainsValid = true,
                        Nature = DiagnosticNature.Structural,
                    });
                }
            }
        }
    }
}
