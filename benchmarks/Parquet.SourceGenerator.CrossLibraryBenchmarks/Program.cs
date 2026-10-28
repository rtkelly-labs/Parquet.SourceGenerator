using System.Collections;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Parquet.SourceGenerator.CrossLibraryBenchmarks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--validate"])
        {
            return await ValidateAsync();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }

    /// <summary>
    /// Runs every benchmark once and checks it did the same work: typed-tier methods must return
    /// rows equal (record value equality) to the generator's; columnar methods must report the
    /// file's row count. A comparand that silently drops nulls, truncates a decimal or shifts a
    /// date fails here rather than looking fast in the table.
    /// </summary>
    private static async Task<int> ValidateAsync()
    {
        int failures = 0;
        foreach (
            object fixture in new object[]
            {
                new TpchTypedRead(),
                new CensusTypedRead(),
                new DiamondsTypedRead(),
            }
        )
        {
            Type type = fixture.GetType();
            Invoke(fixture, "Setup");
            IList expected = (IList)(await InvokeBenchmarkAsync(fixture, "SourceGenerator"))!;
            foreach (MethodInfo method in BenchmarkMethods(type))
            {
                string name = $"{type.Name}.{method.Name}";
                try
                {
                    IList actual = (IList)(await InvokeBenchmarkAsync(fixture, method.Name))!;
                    string? problem = Compare(expected, actual);
                    failures += Report(name, problem ?? $"{actual.Count:N0} rows equal");
                }
                catch (Exception ex)
                {
                    failures += Report(name, ex.GetBaseException().Message, failed: true);
                }
            }

            Invoke(fixture, "Cleanup");
        }

        foreach (Dataset dataset in Enum.GetValues<Dataset>())
        {
            var fixture = new ColumnarReadBenchmark { Dataset = dataset };
            fixture.Setup();
            foreach (MethodInfo method in BenchmarkMethods(typeof(ColumnarReadBenchmark)))
            {
                string name = $"Columnar[{dataset}].{method.Name}";
                try
                {
                    long rows = (long)(await InvokeBenchmarkAsync(fixture, method.Name))!;
                    long want = Datasets.RowCount(dataset);
                    failures += Report(
                        name,
                        rows == want ? $"{rows:N0} rows" : $"{rows:N0} rows, expected {want:N0}",
                        rows != want
                    );
                }
                catch (Exception ex)
                {
                    failures += Report(name, ex.GetBaseException().Message, failed: true);
                }
            }

            fixture.Cleanup();
        }

        Console.WriteLine(failures == 0 ? "All comparands agree." : $"{failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }

    private static string? Compare(IList expected, IList actual)
    {
        if (expected.Count != actual.Count)
        {
            return $"FAIL row count {actual.Count:N0}, expected {expected.Count:N0}";
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!Equals(expected[i], actual[i]))
            {
                return $"FAIL row {i}: {actual[i]} != {expected[i]}";
            }
        }

        return null;
    }

    private static int Report(string name, string message, bool? failed = null)
    {
        bool isFailure = failed ?? message.StartsWith("FAIL", StringComparison.Ordinal);
        Console.WriteLine($"{(isFailure ? "FAIL" : "ok  ")} {name}: {message}");
        return isFailure ? 1 : 0;
    }

    private static IEnumerable<MethodInfo> BenchmarkMethods(Type type) =>
        type.GetMethods().Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null);

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method)!.Invoke(target, null);

    private static async Task<object?> InvokeBenchmarkAsync(object target, string method)
    {
        object? result = target.GetType().GetMethod(method)!.Invoke(target, null);
        if (result is Task task)
        {
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }

        return result;
    }
}
