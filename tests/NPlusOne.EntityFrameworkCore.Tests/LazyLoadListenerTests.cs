using NPlusOne.EntityFrameworkCore.Recording;

namespace NPlusOne.EntityFrameworkCore.Tests;

public sealed class LazyLoadListenerTests
{
    [Fact]
    public void Each_navigation_of_an_entity_keeps_its_own_name()
    {
        var customer = LazyLoadListener.NavigationName(typeof(Order), "Customer");
        var lines = LazyLoadListener.NavigationName(typeof(Order), "Lines");

        Assert.Equal("Order.Customer", customer);
        Assert.Equal("Order.Lines", lines);
        Assert.Same(customer, LazyLoadListener.NavigationName(typeof(Order), "Customer"));
    }
}
