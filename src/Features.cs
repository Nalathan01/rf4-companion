internal static class Features
{
#if PRIVATE || FANGBUCH
    public static readonly bool Log = true;
#else
    public static readonly bool Log = false;
#endif
}
