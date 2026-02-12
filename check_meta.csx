using System;
using System.Reflection;
var asm = Assembly.LoadFrom(@"c:\Users\miche\Desktop\nina.plugin.aiweather\bin\Release\net8.0-windows\NINA.Plugin.AIWeather.dll");
foreach (var a in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
    if (a.Key == "FeaturedImageURL")
        Console.WriteLine($"[{a.Value}] (len={a.Value.Length})");
