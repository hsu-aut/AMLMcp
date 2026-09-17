namespace AmlMcp.Tests;

/// <summary>A temporary folder for test documents, removed afterwards.</summary>
internal sealed class Workspace : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("amlmcp-test-").FullName;

    public string Write(string fileName, string content)
    {
        var path = Path.Combine(Dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Small documents that each exercise one CAEX feature. IDs are fixed so that
/// assertions can refer to them.
/// </summary>
internal static class Fixtures
{
    /// <summary>CAEX 2.15: no XML namespace, links in both legacy forms "ID:Interface" and "Path:Interface".</summary>
    public const string Caex215 = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile FileName="plant215.aml" SchemaVersion="2.15" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="CAEX_ClassModel_V2.15.xsd">
          <InterfaceClassLib Name="AutomationMLInterfaceClassLib">
            <InterfaceClass Name="AutomationMLBaseInterface" />
          </InterfaceClassLib>
          <InstanceHierarchy Name="Plant">
            <InternalElement Name="Pump" ID="{11111111-0000-0000-0000-000000000001}">
              <ExternalInterface Name="Out" ID="{11111111-0000-0000-0000-0000000000a1}" RefBaseClassPath="AutomationMLInterfaceClassLib/AutomationMLBaseInterface" />
            </InternalElement>
            <InternalElement Name="Tank" ID="{11111111-0000-0000-0000-000000000002}">
              <ExternalInterface Name="In" ID="{11111111-0000-0000-0000-0000000000b1}" RefBaseClassPath="AutomationMLInterfaceClassLib/AutomationMLBaseInterface" />
              <ExternalInterface Name="Drain" ID="{11111111-0000-0000-0000-0000000000b2}" RefBaseClassPath="AutomationMLInterfaceClassLib/AutomationMLBaseInterface" />
            </InternalElement>
            <InternalElement Name="Valve" ID="{11111111-0000-0000-0000-000000000003}">
              <ExternalInterface Name="In" ID="{11111111-0000-0000-0000-0000000000c1}" RefBaseClassPath="AutomationMLInterfaceClassLib/AutomationMLBaseInterface" />
            </InternalElement>
            <InternalLink Name="Pipe" RefPartnerSideA="{11111111-0000-0000-0000-000000000001}:Out" RefPartnerSideB="{11111111-0000-0000-0000-000000000002}:In" />
            <InternalLink Name="DrainPipe" RefPartnerSideA="Plant/Tank:Drain" RefPartnerSideB="Plant/Valve:In" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public const string MotorId = "22222222-0000-0000-0000-000000000001";
    public const string MotorPowerInterfaceId = "22222222-0000-0000-0000-0000000000a1";

    /// <summary>
    /// CAEX 3.0 with the cases that used to produce wrong answers: a mirror object
    /// and a mirrored interface, a mirror whose master is missing, an attribute
    /// named like a reference that holds a plain value, a real dangling reference,
    /// a typed cross-hierarchy reference, class inheritance over two levels and a
    /// layout attribute.
    /// </summary>
    public const string Tricky30 = $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="tricky.aml" xmlns="http://www.dke.de/CAEX">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <InterfaceClassLib Name="AutomationMLInterfaceClassLib">
            <InterfaceClass Name="AutomationMLBaseInterface" />
          </InterfaceClassLib>
          <AttributeTypeLib Name="AutomationML_ObjectReferences_AttributeTypeLib">
            <AttributeType Name="refObj" AttributeDataType="xs:string" />
          </AttributeTypeLib>
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Drive" ID="{22222222-0000-0000-0000-00000000c001}">
              <Description>Any electric drive.</Description>
              <Attribute Name="Manufacturer" AttributeDataType="xs:string"><DefaultValue>ACME</DefaultValue></Attribute>
            </SystemUnitClass>
            <SystemUnitClass Name="Motor" ID="{22222222-0000-0000-0000-00000000c002}" RefBaseClassPath="Lib/Drive">
              <Attribute Name="RatedPower" AttributeDataType="xs:double" Unit="kW"><DefaultValue>7.5</DefaultValue></Attribute>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{22222222-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Motor1" ID="{{{MotorId}}}" RefBaseSystemUnitPath="Lib/Motor">
              <Attribute Name="refTemperature" AttributeDataType="xs:double" Unit="degC"><Value>25</Value></Attribute>
              <Attribute Name="refCustomer" AttributeDataType="xs:string"><Value>{99999999-9999-9999-9999-999999999999}</Value></Attribute>
              <Attribute Name="ViewInformation" AttributeDataType="xs:string">
                <Attribute Name="x" AttributeDataType="xs:double"><Value>10</Value></Attribute>
              </Attribute>
              <ExternalInterface Name="Power" ID="{{{MotorPowerInterfaceId}}}" RefBaseClassPath="AutomationMLInterfaceClassLib/AutomationMLBaseInterface" />
            </InternalElement>
            <InternalElement Name="Motor2" ID="{22222222-0000-0000-0000-000000000002}" RefBaseSystemUnitPath="Lib/Motor">
              <Attribute Name="RatedPower" AttributeDataType="xs:double" Unit="kW"><Value>11</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
          <InstanceHierarchy Name="Groups" ID="{22222222-0000-0000-0000-0000000000f2}">
            <InternalElement Name="Motor1" ID="{22222222-0000-0000-0000-000000000011}" RefBaseSystemUnitPath="{{{MotorId}}}">
              <ExternalInterface Name="Power" ID="{22222222-0000-0000-0000-0000000000b1}" RefBaseClassPath="{{{MotorPowerInterfaceId}}}" />
            </InternalElement>
            <InternalElement Name="Ghost" ID="{22222222-0000-0000-0000-000000000012}" RefBaseSystemUnitPath="{77777777-7777-7777-7777-777777777777}" />
            <InternalElement Name="Note" ID="{22222222-0000-0000-0000-000000000013}">
              <Attribute Name="refObj" AttributeDataType="xs:string" RefAttributeType="ObjectReferences@AutomationML_ObjectReferences_AttributeTypeLib/refObj"><Value>{{MotorId}}</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public const string StationId = "55555555-0000-0000-0000-000000000001";
    public const string StationPortId = "55555555-0000-0000-0000-0000000000a1";
    public const string Station2PortId = "55555555-0000-0000-0000-0000000000b1";
    public const string Station3PortId = "55555555-0000-0000-0000-0000000000c1";
    public const string DuplicatedId = "55555555-0000-0000-0000-000000000007";
    public const string CaseVariantIdLower = "55555555-0000-0000-0000-00000000000a";
    public const string CaseVariantIdUpper = "55555555-0000-0000-0000-00000000000A";
    public const string MissingPartnerId = "99999999-9999-9999-9999-999999999999";

    /// <summary>
    /// CAEX 3.0 in its mainline form, with what the other fixtures do not have:
    /// InternalLinks whose partners are plain interface IDs (not the 2.15
    /// "owner:Interface" form), one interface used by two links, a link whose
    /// partner does not exist, an ID that really is used twice, two IDs that
    /// differ only in letter case, an attribute typed xs:IDREF that points at an
    /// element, a layout attribute typed with an OMG Diagram Definition type, a
    /// RoleRequirements element, descriptions, nested children, and a mirror
    /// reference that is in truth a class name without its library.
    /// </summary>
    public const string Rich30 = $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="rich.aml" xmlns="http://www.dke.de/CAEX">
          <SuperiorStandardVersion>AutomationML 2.10</SuperiorStandardVersion>
          <InterfaceClassLib Name="RichInterfaceLib">
            <InterfaceClass Name="Coupling">
              <Description>Connection point between two stations.</Description>
            </InterfaceClass>
          </InterfaceClassLib>
          <RoleClassLib Name="RichRoleLib">
            <RoleClass Name="Station">
              <Description>A station of the plant.</Description>
            </RoleClass>
          </RoleClassLib>
          <AttributeTypeLib Name="OMG_DD_AttributeTypeLib">
            <AttributeType Name="DD_Bounds" AttributeDataType="xs:string">
              <Description>Diagram Definition bounds: layout, not engineering data.</Description>
            </AttributeType>
          </AttributeTypeLib>
          <SystemUnitClassLib Name="RichUnitLib">
            <SystemUnitClass Name="Drill" ID="{55555555-0000-0000-0000-00000000c001}">
              <Description>A drilling unit.</Description>
              <Attribute Name="SpindleSpeed" AttributeDataType="xs:double" Unit="1/min"><DefaultValue>3000</DefaultValue></Attribute>
              <Attribute Name="Vendor" AttributeDataType="xs:string"><Value>ACME</Value></Attribute>
              <Attribute Name="Coolant" AttributeDataType="xs:string"><DefaultValue>water</DefaultValue><Value>oil</Value></Attribute>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Shopfloor" ID="{55555555-0000-0000-0000-0000000000f1}">
            <Description>One line with three stations.</Description>
            <InternalElement Name="Station1" ID="{{{StationId}}}" RefBaseSystemUnitPath="RichUnitLib/Drill">
              <Description>The drilling station of the line.</Description>
              <Attribute Name="Feed" AttributeDataType="xs:double" Unit="mm/s"><DefaultValue>4.5</DefaultValue></Attribute>
              <Attribute Name="Bounds" AttributeDataType="xs:string" RefAttributeType="OMG_DD@OMG_DD_AttributeTypeLib/DD_Bounds">
                <Attribute Name="width" AttributeDataType="xs:double"><Value>120</Value></Attribute>
              </Attribute>
              <ExternalInterface Name="Port" ID="{{{StationPortId}}}" RefBaseClassPath="RichInterfaceLib/Coupling" />
              <InternalElement Name="Spindle" ID="{55555555-0000-0000-0000-000000000002}">
                <InternalElement Name="Tool" ID="{55555555-0000-0000-0000-000000000003}" />
              </InternalElement>
              <RoleRequirements RefBaseRoleClassPath="RichRoleLib/Station" />
            </InternalElement>
            <InternalElement Name="Station2" ID="{55555555-0000-0000-0000-000000000004}">
              <Attribute Name="Origin" AttributeDataType="xs:IDREF"><Value>{{{StationId}}}</Value></Attribute>
              <ExternalInterface Name="Port" ID="{{{Station2PortId}}}" RefBaseClassPath="RichInterfaceLib/Coupling" />
            </InternalElement>
            <InternalElement Name="Station3" ID="{55555555-0000-0000-0000-000000000005}">
              <ExternalInterface Name="Port" ID="{{{Station3PortId}}}" RefBaseClassPath="RichInterfaceLib/Coupling" />
            </InternalElement>
            <InternalElement Name="Twin" ID="{55555555-0000-0000-0000-000000000006}" RefBaseSystemUnitPath="Drill" />
            <InternalElement Name="Spare" ID="{{{DuplicatedId}}}" />
            <InternalElement Name="SpareCopy" ID="{{{DuplicatedId}}}" />
            <InternalElement Name="Cased" ID="{{{CaseVariantIdLower}}}" />
            <InternalElement Name="CasedUpper" ID="{{{CaseVariantIdUpper}}}" />
            <InternalLink Name="OneToTwo" ID="{55555555-0000-0000-0000-0000000000e1}"
                          RefPartnerSideA="{{{StationPortId}}}" RefPartnerSideB="{{{Station2PortId}}}" />
            <InternalLink Name="OneToThree" ID="{55555555-0000-0000-0000-0000000000e2}"
                          RefPartnerSideA="{{{StationPortId}}}" RefPartnerSideB="{{{Station3PortId}}}" />
            <InternalLink Name="Nowhere" ID="{55555555-0000-0000-0000-0000000000e3}"
                          RefPartnerSideA="{{{Station2PortId}}}" RefPartnerSideB="{{{MissingPartnerId}}}" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public const string ExternalLibrary = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="MotorLib.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="MotorLib">
            <SystemUnitClass Name="Motor">
              <Description>A motor from an external library.</Description>
              <Attribute Name="RatedPower" AttributeDataType="xs:double" Unit="kW"><DefaultValue>3</DefaultValue></Attribute>
            </SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    public static string UsingLibrary(string libraryFile) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="user.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="{{libraryFile}}" Alias="Motors" />
          <InstanceHierarchy Name="Plant" ID="{33333333-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{33333333-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Motors@MotorLib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;
}
