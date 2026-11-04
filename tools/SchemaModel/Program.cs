// Turns an official ISO 20022 XSD into a compact JSON model for the browser viewer's validator.
// Usage: dotnet run -- schemas/pacs.008.001.08.xsd out.json
// ISO 20022 schemas use a small part of XML Schema: named complex types with a sequence or a choice of
// elements, simple content with one attribute (amounts with Ccy), and simple types restricting a built-in
// type with pattern, length, enumeration, digits and inclusive bounds. Anything else is reported and stops the tool.
using System.Text.Json;
using System.Xml;
using System.Xml.Schema;

var set = new XmlSchemaSet { XmlResolver = null };
using (var r = XmlReader.Create(args[0], new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
    set.Add(XmlSchema.Read(r, null)!);
set.Compile();
var schema = set.Schemas().Cast<XmlSchema>().Single();

var types = new SortedDictionary<string, object>(StringComparer.Ordinal);

static string Builtin(XmlSchemaDatatype dt) => dt.TypeCode switch
{
    XmlTypeCode.String or XmlTypeCode.NormalizedString or XmlTypeCode.Token => "string",
    XmlTypeCode.Decimal => "decimal",
    XmlTypeCode.Boolean => "boolean",
    XmlTypeCode.Date => "date",
    XmlTypeCode.DateTime => "dateTime",
    XmlTypeCode.Time => "time",
    XmlTypeCode.GYear => "gYear",
    XmlTypeCode.GYearMonth => "gYearMonth",
    XmlTypeCode.Base64Binary => "base64Binary",
    _ => throw new NotSupportedException($"built-in type {dt.TypeCode}"),
};

object Simple(XmlSchemaSimpleType st)
{
    var o = new Dictionary<string, object> { ["kind"] = "simple", ["base"] = Builtin(st.Datatype!) };
    if (st.Content is XmlSchemaSimpleTypeRestriction res)
    {
        var patterns = new List<string>();
        var enums = new List<string>();
        foreach (var f in res.Facets.Cast<XmlSchemaFacet>())
        {
            switch (f)
            {
                case XmlSchemaPatternFacet p: patterns.Add(p.Value!); break;
                case XmlSchemaEnumerationFacet e: enums.Add(e.Value!); break;
                case XmlSchemaMinLengthFacet m: o["minLength"] = int.Parse(m.Value!); break;
                case XmlSchemaMaxLengthFacet m: o["maxLength"] = int.Parse(m.Value!); break;
                case XmlSchemaLengthFacet m: o["length"] = int.Parse(m.Value!); break;
                case XmlSchemaTotalDigitsFacet t: o["totalDigits"] = int.Parse(t.Value!); break;
                case XmlSchemaFractionDigitsFacet t: o["fractionDigits"] = int.Parse(t.Value!); break;
                case XmlSchemaMinInclusiveFacet t: o["minInclusive"] = t.Value!; break;
                case XmlSchemaMaxInclusiveFacet t: o["maxInclusive"] = t.Value!; break;
                case XmlSchemaMinExclusiveFacet t: o["minExclusive"] = t.Value!; break;
                default: throw new NotSupportedException($"facet {f.GetType().Name} in {st.Name}");
            }
        }
        if (patterns.Count > 0) o["patterns"] = patterns;
        if (enums.Count > 0) o["enum"] = enums;
    }
    return o;
}

string TypeName(XmlQualifiedName q, XmlSchemaType? t)
{
    if (q.Namespace == XmlSchema.Namespace) return "xs:" + Builtin(t!.Datatype!);
    return q.Name;
}

foreach (var t in schema.SchemaTypes.Values.Cast<XmlSchemaType>())
{
    switch (t)
    {
        case XmlSchemaSimpleType st:
            types[st.Name!] = Simple(st);
            break;
        case XmlSchemaComplexType ct when ct.ContentModel is XmlSchemaSimpleContent sc:
        {
            var ext = (XmlSchemaSimpleContentExtension)sc.Content!;
            var attrs = ext.Attributes.Cast<XmlSchemaAttribute>().Select(a => new Dictionary<string, object>
            {
                ["name"] = a.Name!, ["type"] = a.SchemaTypeName.Name, ["required"] = a.Use == XmlSchemaUse.Required,
            }).ToList();
            types[ct.Name!] = new Dictionary<string, object> { ["kind"] = "simpleContent", ["value"] = ext.BaseTypeName.Name, ["attributes"] = attrs };
            break;
        }
        case XmlSchemaComplexType ct:
        {
            var particle = ct.Particle;
            string content; XmlSchemaObjectCollection items;
            switch (particle)
            {
                case XmlSchemaSequence s: content = "sequence"; items = s.Items; break;
                case XmlSchemaChoice c: content = "choice"; items = c.Items; break;
                case null: content = "empty"; items = []; break;
                default: throw new NotSupportedException($"particle {particle.GetType().Name} in {ct.Name}");
            }
            var children = new List<object>();
            foreach (var item in items)
            {
                if (item is XmlSchemaAny any)
                {
                    children.Add(new Dictionary<string, object> { ["any"] = true, ["min"] = (int)any.MinOccurs, ["max"] = any.MaxOccurs == decimal.MaxValue ? -1 : (int)any.MaxOccurs });
                    continue;
                }
                var e = (XmlSchemaElement)item;
                children.Add(new Dictionary<string, object>
                {
                    ["name"] = e.Name!,
                    ["type"] = TypeName(e.SchemaTypeName, e.ElementSchemaType),
                    ["min"] = (int)e.MinOccurs,
                    ["max"] = e.MaxOccurs == decimal.MaxValue ? -1 : (int)e.MaxOccurs,
                });
            }
            if (ct.Attributes.Count > 0) throw new NotSupportedException($"attributes on complex type {ct.Name}");
            types[ct.Name!] = new Dictionary<string, object> { ["kind"] = "complex", ["content"] = content, ["children"] = children };
            break;
        }
    }
}

var root = schema.Elements.Values.Cast<XmlSchemaElement>().Single();
var model = new { message = schema.TargetNamespace!.Split(':').Last(), @namespace = schema.TargetNamespace, root = root.Name, rootType = root.SchemaTypeName.Name, types };
File.WriteAllText(args[1], JsonSerializer.Serialize(model) + "\n");
Console.WriteLine($"{args[1]}: {types.Count} types");
