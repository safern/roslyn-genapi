using System;
#nullable enable

namespace TestAssembly
{
    internal class MyAttribute : Attribute { }

    public class GenericClass<T> where T : class { }

    [My]
    public class Class1
    {
        [return: My]
        public string? Foo() { throw null!; }
    }
}
