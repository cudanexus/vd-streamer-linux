using System;
using System.IO;
using VDStreamer;
using VDStreamer.Vendor;

// Usage:
//   vdstreamer-vendor --pkg VirtualDesktop.Streamer.Setup.pkg --target build/vendor
//   vdstreamer-vendor --facade --vendor build/vendor --refpack <ref/net8.0> --out build/vendor-ref/System.Private.CoreLib.dll
string pkg = null, target = null, vendor = null, refpack = null, output = null;
bool facade = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pkg": pkg = args[++i]; break;
        case "--target": target = args[++i]; break;
        case "--facade": facade = true; break;
        case "--vendor": vendor = args[++i]; break;
        case "--refpack": refpack = args[++i]; break;
        case "--out": output = args[++i]; break;
    }
}
try
{
    if (facade)
    {
        if (vendor == null || refpack == null || output == null)
        {
            Console.Error.WriteLine("usage: vdstreamer-vendor --facade --vendor DIR --refpack DIR --out FILE");
            return 64;
        }
        CoreLibFacade.Generate(vendor, refpack, output);
        return 0;
    }
    if (pkg == null || target == null)
    {
        Console.Error.WriteLine("usage: vdstreamer-vendor --pkg FILE --target DIR");
        return 64;
    }
    VendorManager.InstallFromPkg(Path.GetFullPath(pkg), Path.GetFullPath(target), Console.WriteLine);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex);
    return 1;
}
