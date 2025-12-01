using PX.Data;
using PX.Objects.IN;
using PX.Objects.AR;
using PX.Objects.SO;
using StandingOrders;          // namespace that contains Series DAC

// Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
namespace StandingOrders
{
    public class orderTypeST : PX.Data.BQL.BqlString.Constant<orderTypeST>
    {
        public orderTypeST() : base("ST") { }
    }

    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public sealed class SOOrderExt : PXCacheExtension<SOOrder>
{

    #region UsrBookSeriesCD
    [PXDBInt]
    [PXUIField(DisplayName = STMessages.BookSeries)]
    [PXUIVisible(typeof(Where<SOOrder.orderType.IsEqual<orderTypeST>>))]
        [PXDefault(PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIRequired(typeof(Where<SOOrder.orderType.IsEqual<orderTypeST>>))] // make it required only for ST orders
        [PXUIEnabled(typeof(Where<SOOrder.customerID, IsNotNull, And<SOOrder.customerLocationID, IsNotNull>>))] // Enable only if there is a customer selected
        [PXSelector(
    typeof(Search2<
        InventoryItem.inventoryID,                          // key stored
        InnerJoin<Series,
            On<Series.bookSeriesCD, Equal<InventoryItem.inventoryID>>>>), // filter
    SubstituteKey = typeof(InventoryItem.inventoryCD),      // what user sees
    DescriptionField = typeof(InventoryItem.descr),         // description
    ValidateValue = false)]// tooltip/description
        public int? UsrBookSeriesCD { get; set; }
    public abstract class usrBookSeriesCD : PX.Data.BQL.BqlInt.Field<usrBookSeriesCD> { }
    #endregion
}

}