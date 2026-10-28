using System.Runtime.CompilerServices;

// NullableColumnExtractor and VectorizedColumnTransforms are internal (#461): no generated code
// calls them, so they are not part of the package surface. Their own unit tests and the
// NullBitmapExtractionBenchmark probe still exercise them directly.
[assembly: InternalsVisibleTo("Parquet.SourceGenerator.Tests")]
[assembly: InternalsVisibleTo("Parquet.SourceGenerator.Benchmarks")]
