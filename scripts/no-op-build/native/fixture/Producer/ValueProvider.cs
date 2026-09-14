namespace NativeFixture;

public static class ValueProvider
{
#if NATIVE_PROBE_DEFINE
    public static string Value => "defined";
#else
    public static string Value => "one";
#endif
}
