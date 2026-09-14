using System.Reflection;
System.Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
