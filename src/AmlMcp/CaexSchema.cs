using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace AmlMcp;

/// <summary>
/// The CAEX 3.0 schema itself, carried in the assembly and asked at the moment a document
/// is written. The writer builds a conformant document by construction; this proves it.
/// The difference matters for an input nobody thought of: the structural tests only know
/// the cases their author imagined, the schema knows all of them.
///
/// <para>
/// `schema/CAEX_ClassModel_V.3.0.xsd` is the schema published with IEC 62714 by
/// AutomationML e.V., unchanged. It is read only, never written, and is used here the same
/// way Aml.Engine and AmlEngine.js carry it.
/// </para>
/// </summary>
public static class CaexSchema
{
    private const string ResourceName = "AmlMcp.CaexSchema30.xsd";

    private static readonly Lazy<XmlSchemaSet> Set = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// What the schema has against this document, one line per fault, each naming the
    /// element it sits on. An empty list is the whole promise: a tool that validates will
    /// open the document.
    /// </summary>
    public static IReadOnlyList<string> Validate(XElement document)
    {
        var faults = new List<string>();
        // A copy: validating attaches schema information to the tree, and the caller's tree
        // is going to be written out as it stands.
        new XDocument(new XElement(document)).Validate(Set.Value, (_, e) =>
        {
            var where = e.Exception is { } x && x.LineNumber > 0 ? $" (line {x.LineNumber})" : "";
            faults.Add(e.Message + where);
        });
        return faults;
    }

    private static XmlSchemaSet Load()
    {
        using var stream = typeof(CaexSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The CAEX schema is missing from this build ({ResourceName}).");
        using var reader = XmlReader.Create(stream);
        var set = new XmlSchemaSet();
        set.Add(AmlModel.C.NamespaceName, reader);
        set.Compile();
        return set;
    }
}
