# The generated code's call graph — documentation (#252)

Static view of the golden models' emitted code: what a consumer's debugger walks.
Same extractor, same limits (delegates/virtuals invisible). Not a gate — the golden
files themselves are the contract; this page is the map. Refresh alongside the
call-graph baselines: `UPDATE_GOLDEN_FILES=true dotnet run scripts/CallGraph.cs`.

## LegacyRecordParquetLegacyExtensions.g

```mermaid
graph TD
    tsampledomainmodelslegacyrecordparquetlegacyextensions_055["SampleDomain.Models.LegacyRecordParquetLegacyExtensions"]
    tsampledomainmodelslegacyrecordparquetlegacyextensionsstringdeduplicator_074["SampleDomain.Models.LegacyRecordParquetLegacyExtensions.StringDeduplicator"]
    tsampledomainmodelslegacyrecordparquetlegacyextensions_055 -->|1| tsampledomainmodelslegacyrecordparquetlegacyextensionsstringdeduplicator_074
```

## ListOrderParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelslistorderparquetextensions_046["SampleDomain.Models.ListOrderParquetExtensions"]
    tsampledomainmodelslistorderparquetmemorysource_048["SampleDomain.Models.ListOrderParquetMemorySource"]
    tsampledomainmodelslistorderparquetparallelsource_050["SampleDomain.Models.ListOrderParquetParallelSource"]
    tsampledomainmodelslistorderparquetstreamsource_048["SampleDomain.Models.ListOrderParquetStreamSource"]
    tsampledomainmodelslistorderparquetmemorysource_048 -->|2| tsampledomainmodelslistorderparquetextensions_046
    tsampledomainmodelslistorderparquetparallelsource_050 -->|2| tsampledomainmodelslistorderparquetextensions_046
    tsampledomainmodelslistorderparquetstreamsource_048 -->|2| tsampledomainmodelslistorderparquetextensions_046
```

## NestedOrderParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelsnestedorderparquetextensions_048["SampleDomain.Models.NestedOrderParquetExtensions"]
    tsampledomainmodelsnestedorderparquetmemorysource_050["SampleDomain.Models.NestedOrderParquetMemorySource"]
    tsampledomainmodelsnestedorderparquetparallelsource_052["SampleDomain.Models.NestedOrderParquetParallelSource"]
    tsampledomainmodelsnestedorderparquetstreamsource_050["SampleDomain.Models.NestedOrderParquetStreamSource"]
    tsampledomainmodelsnestedorderparquetmemorysource_050 -->|2| tsampledomainmodelsnestedorderparquetextensions_048
    tsampledomainmodelsnestedorderparquetparallelsource_052 -->|2| tsampledomainmodelsnestedorderparquetextensions_048
    tsampledomainmodelsnestedorderparquetstreamsource_050 -->|2| tsampledomainmodelsnestedorderparquetextensions_048
```

## OrderEventParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelsordereventparquetextensions_047["SampleDomain.Models.OrderEventParquetExtensions"]
    tsampledomainmodelsordereventparquetextensionsstringdeduplicator_066["SampleDomain.Models.OrderEventParquetExtensions.StringDeduplicator"]
    tsampledomainmodelsordereventparquetmemorysource_049["SampleDomain.Models.OrderEventParquetMemorySource"]
    tsampledomainmodelsordereventparquetparallelsource_051["SampleDomain.Models.OrderEventParquetParallelSource"]
    tsampledomainmodelsordereventparquetstreamsource_049["SampleDomain.Models.OrderEventParquetStreamSource"]
    tsampledomainmodelsordereventparquetextensions_047 -->|7| tsampledomainmodelsordereventparquetextensionsstringdeduplicator_066
    tsampledomainmodelsordereventparquetmemorysource_049 -->|3| tsampledomainmodelsordereventparquetextensions_047
    tsampledomainmodelsordereventparquetparallelsource_051 -->|2| tsampledomainmodelsordereventparquetextensions_047
    tsampledomainmodelsordereventparquetstreamsource_049 -->|3| tsampledomainmodelsordereventparquetextensions_047
```

## PocoOrderParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelspocoorderparquetextensions_046["SampleDomain.Models.PocoOrderParquetExtensions"]
    tsampledomainmodelspocoorderparquetmemorysource_048["SampleDomain.Models.PocoOrderParquetMemorySource"]
    tsampledomainmodelspocoorderparquetparallelsource_050["SampleDomain.Models.PocoOrderParquetParallelSource"]
    tsampledomainmodelspocoorderparquetstreamsource_048["SampleDomain.Models.PocoOrderParquetStreamSource"]
    tsampledomainmodelspocoorderparquetmemorysource_048 -->|2| tsampledomainmodelspocoorderparquetextensions_046
    tsampledomainmodelspocoorderparquetparallelsource_050 -->|2| tsampledomainmodelspocoorderparquetextensions_046
    tsampledomainmodelspocoorderparquetstreamsource_048 -->|2| tsampledomainmodelspocoorderparquetextensions_046
```

## ScalarMetricParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelsscalarmetricparquetextensions_049["SampleDomain.Models.ScalarMetricParquetExtensions"]
    tsampledomainmodelsscalarmetricparquetmemorysource_051["SampleDomain.Models.ScalarMetricParquetMemorySource"]
    tsampledomainmodelsscalarmetricparquetparallelsource_053["SampleDomain.Models.ScalarMetricParquetParallelSource"]
    tsampledomainmodelsscalarmetricparquetstreamsource_051["SampleDomain.Models.ScalarMetricParquetStreamSource"]
    tsampledomainmodelsscalarmetricparquetmemorysource_051 -->|3| tsampledomainmodelsscalarmetricparquetextensions_049
    tsampledomainmodelsscalarmetricparquetparallelsource_053 -->|2| tsampledomainmodelsscalarmetricparquetextensions_049
    tsampledomainmodelsscalarmetricparquetstreamsource_051 -->|3| tsampledomainmodelsscalarmetricparquetextensions_049
```

## SortedShipmentParquetExtensions.g

```mermaid
graph TD
    tsampledomainmodelssortedshipmentparquetextensions_051["SampleDomain.Models.SortedShipmentParquetExtensions"]
    tsampledomainmodelssortedshipmentparquetextensionsstringdeduplicator_070["SampleDomain.Models.SortedShipmentParquetExtensions.StringDeduplicator"]
    tsampledomainmodelssortedshipmentparquetmemorysource_053["SampleDomain.Models.SortedShipmentParquetMemorySource"]
    tsampledomainmodelssortedshipmentparquetparallelsource_055["SampleDomain.Models.SortedShipmentParquetParallelSource"]
    tsampledomainmodelssortedshipmentparquetstreamsource_053["SampleDomain.Models.SortedShipmentParquetStreamSource"]
    tsampledomainmodelssortedshipmentparquetextensions_051 -->|8| tsampledomainmodelssortedshipmentparquetextensionsstringdeduplicator_070
    tsampledomainmodelssortedshipmentparquetmemorysource_053 -->|3| tsampledomainmodelssortedshipmentparquetextensions_051
    tsampledomainmodelssortedshipmentparquetparallelsource_055 -->|2| tsampledomainmodelssortedshipmentparquetextensions_051
    tsampledomainmodelssortedshipmentparquetstreamsource_053 -->|3| tsampledomainmodelssortedshipmentparquetextensions_051
```

