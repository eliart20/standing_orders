using PX.Data;
using PX.Objects.IN;
using PX.Objects.AR;
using PX.Objects.SO;
using StandingOrders;          // namespace that contains Series DAC

// Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
namespace StandingOrders
{

    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public sealed class SOOrderFilterExt : PXCacheExtension<SOOrderFilter>

{

    public class actionShip : PX.Data.BQL.BqlString.Constant<orderTypeST>
    {
        public actionShip() : base(SOCreateShipment.WellKnownActions.SOOrderScreen.CreateChildOrders) { }
    }


    #region UsrBookSeriesCD
    [PXInt]
    [PXUIField(DisplayName = STMessages.BookSeries)]
    [PXUIVisible(typeof(Where<SOOrderFilter.action.IsEqual<actionShip>>))]
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