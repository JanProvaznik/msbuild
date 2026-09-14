System.Console.WriteLine(NativeFixture.ValueProvider.Value);
System.Console.WriteLine(System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "payload.txt")).Trim());
