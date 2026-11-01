internal static class Features
{
#if PRIVATE || FANGBUCH
    public static readonly bool Log = true;
#else
    public static readonly bool Log = false;
#endif
#if PRIVATE
    public static readonly bool Tracker = true;
    public static readonly bool BaitHistory = true;
#else
    public static readonly bool Tracker = false;
    public static readonly bool BaitHistory = false;
#endif
}
