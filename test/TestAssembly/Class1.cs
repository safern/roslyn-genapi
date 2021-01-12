using System;
#nullable enable

/*
TODO: support structs, attributes, enum underlying type.
*/
namespace TestAssembly
{
    internal class MyAttribute : Attribute { }

    public class GenericClass<T> { }
    public class GenericClassWithConstraints<T> where T : class { }

    [My]
    public class Class1
    {
        [return: My]
        public string? Foo() { throw null!; }
    }

    public enum MyEnum
    {
        Foo,
        Blah
    }
    [Flags]
    public enum MyFlagEnum
    {
        Foo=0x01,
        Blah=0x02,
        Duh=0x04
    }
    public enum MyEnumWithBaseType : ushort
    {
        Foo,
        Blah
    }
}
