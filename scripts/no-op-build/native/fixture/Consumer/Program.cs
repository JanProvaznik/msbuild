if (args.Length != 0 && args[0] == "--company")
{
    System.Console.WriteLine(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyCompanyAttribute>(System.Reflection.Assembly.GetExecutingAssembly())?.Company);
    return;
}
System.Console.WriteLine(NativeFixture.ValueProvider.Value);
System.Console.WriteLine(System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "payload.txt")).Trim());
