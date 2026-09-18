using System.Xml;
using System.Xml.Serialization;

namespace Shared.GameFormats.Vmd;

internal sealed class UnknownXmlDataThrower
{
    public XmlDeserializationEvents EventHandler { get; } = new()
    {
        OnUnknownAttribute = ThrowAttribute,
        OnUnknownNode = ThrowNode,
        OnUnknownElement = ThrowElement,
    };

    private static void ThrowAttribute(object? sender, XmlAttributeEventArgs e)
        => throw new XmlException($"Unsupported xml attribute: {e.Attr.LocalName} at line {e.LineNumber} and position {e.LinePosition}", null, e.LineNumber, e.LinePosition);

    private static void ThrowNode(object? sender, XmlNodeEventArgs e)
        => throw new XmlException($"Unsupported xml node: {e.LocalName} at line {e.LineNumber} and position {e.LinePosition}", null, e.LineNumber, e.LinePosition);

    private static void ThrowElement(object? sender, XmlElementEventArgs e)
        => throw new XmlException($"Unsupported xml element: {e.Element.LocalName} at line {e.LineNumber} and position {e.LinePosition}", null, e.LineNumber, e.LinePosition);
}
