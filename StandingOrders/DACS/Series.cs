using PX.Data;
using PX.Data.BQL;
using PX.Objects.IN;
using System;

namespace StandingOrders
{
    [Serializable]
    [PXCacheName("Series")]
    public class Series : PXBqlTable, IBqlTable
    {
        #region Selected
        [PXBool]
        [PXUnboundDefault(false)]
        [PXUIField(DisplayName = "Selected")]
        public virtual bool? Selected { get; set; }
        public abstract class selected : BqlBool.Field<selected> { }
        #endregion

        #region BookSeriesID
        [PXDBIdentity]
        public virtual int? BookSeriesID { get; set; }
        public abstract class bookSeriesID : PX.Data.BQL.BqlInt.Field<bookSeriesID> { }
        #endregion

        public class ItemSeriesClass : PX.Data.BQL.BqlString.Constant<ItemSeriesClass>
        {
            public ItemSeriesClass() : base("NSTOCK    ST STANDING  ") { }
        }

        #region BookSeriesCD
        [PXDBInt(IsKey =true)]
        [PXUIField(DisplayName = "Book Series ID")]
        [PXSelector(
    typeof(Search2<InventoryItem.inventoryID,
                   InnerJoin<INItemClass,
                     On<INItemClass.itemClassID, Equal<InventoryItem.itemClassID>>>,
                   Where<InventoryItem.itemStatus,
                         In3<InventoryItemStatus.active, InventoryItemStatus.noSales>,
                     And<INItemClass.itemClassCD,
                         Equal<ItemSeriesClass>>>>),          // <-- filter
    SubstituteKey = typeof(InventoryItem.inventoryCD),
    DescriptionField = typeof(InventoryItem.descr))]
        public virtual int? BookSeriesCD { get; set; }
        public abstract class bookSeriesCD : PX.Data.BQL.BqlInt.Field<bookSeriesCD> { }
        #endregion

        #region BookSeriesName
        [PXDBString(30, InputMask = "")]
        [PXUIField(DisplayName = "Book Series Name")]
        public virtual string BookSeriesName { get; set; }
        public abstract class bookSeriesName : PX.Data.BQL.BqlString.Field<bookSeriesName> { }
        #endregion

        #region CycleID
        [PXDBInt]
        [PXUIField(DisplayName = "Cycle ID")]
        [PXSelector(
            typeof(Search<Cycle.cycleID>),
            SubstituteKey = typeof(Cycle.cycleName),
            DescriptionField = typeof(Cycle.cycleName)
        )]
        public virtual int? CycleID { get; set; }
        public abstract class cycleID : PX.Data.BQL.BqlInt.Field<cycleID> { }
        #endregion

        #region Default Lead Time
        [PXDBInt]
        [PXUIField(DisplayName = "Default Lead Time")]
        public virtual int? DefaultLeadTime { get; set; }
        public abstract class defaultLeadTime : PX.Data.BQL.BqlInt.Field<defaultLeadTime> { }
        #endregion
    }
}