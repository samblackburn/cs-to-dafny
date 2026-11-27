// Enums become datatypes with nullary constructors; switches over them become match.
// verify: 0
namespace Traffic;

public enum Light { Red, Amber, Green }

public static class Signals
{
    public static Light Next(Light l) => l switch
    {
        Light.Red => Light.Green,
        Light.Green => Light.Amber,
        Light.Amber => Light.Red,
        _ => throw new System.ArgumentOutOfRangeException(nameof(l)),
    };

    public static bool CanGo(Light l) => l == Light.Green;

    public static bool MustStop(Light l) => l is Light.Red or Light.Amber;

    public static int Seconds(Light l)
    {
        switch (l)
        {
            case Light.Red:
                return 30;
            case Light.Amber:
                return 3;
            default:
                return 25;
        }
    }
}
