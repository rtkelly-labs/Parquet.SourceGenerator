using Parquet.SourceGenerator;
using Parquet.SourceGenerator.NodaTime.Adapters;

// The package's default registrations. Referencing this assembly is what makes the adapters
// discoverable: the generator reads these attributes from referenced assemblies at compile time
// and never scans for conversion methods. Only the lossless defaults are registered — the
// native-interop adapters in InteropAdapters.cs narrow the value domain, so they are chosen per
// member with [ParquetAdapter].
[assembly: ParquetTypeAdapter(typeof(InstantAdapter))]
[assembly: ParquetTypeAdapter(typeof(DurationAdapter))]
[assembly: ParquetTypeAdapter(typeof(LocalDateAdapter))]
[assembly: ParquetTypeAdapter(typeof(LocalTimeAdapter))]
[assembly: ParquetTypeAdapter(typeof(LocalDateTimeAdapter))]
[assembly: ParquetTypeAdapter(typeof(OffsetAdapter))]
[assembly: ParquetTypeAdapter(typeof(OffsetDateTimeAdapter))]
[assembly: ParquetTypeAdapter(typeof(OffsetDateAdapter))]
[assembly: ParquetTypeAdapter(typeof(OffsetTimeAdapter))]
[assembly: ParquetTypeAdapter(typeof(IntervalAdapter))]
[assembly: ParquetTypeAdapter(typeof(DateIntervalAdapter))]
[assembly: ParquetTypeAdapter(typeof(YearMonthAdapter))]
[assembly: ParquetTypeAdapter(typeof(AnnualDateAdapter))]
[assembly: ParquetTypeAdapter(typeof(PeriodAdapter))]
[assembly: ParquetTypeAdapter(typeof(ZonedDateTimeAdapter))]
