namespace GateFixture;

// RS0016: public API that is in no PublicAPI.*.txt.
public sealed class Exposed
{
    public int Uncatalogued() => 1;
}

// PARQAPI002: a member widened past private with no line in seams.txt.
internal static class Seam
{
    internal static int Widened() => 1;
}

// CA1502: cyclomatic complexity above the fixture threshold.
internal static class Branchy
{
    private static int Complex(int x)
    {
        if (x == 1)
        {
            return 1;
        }

        if (x == 2)
        {
            return 2;
        }

        return x == 3 ? 3 : 4;
    }

    public static int Use() => Complex(1);
}
