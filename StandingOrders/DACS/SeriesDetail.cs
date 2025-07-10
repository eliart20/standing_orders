using System;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;

namespace StandingOrders
{
    [Serializable, PXCacheName("SeriesDetail")]
    public class SeriesDetail : PXBqlTable, IBqlTable
    {
        // ────────────────────────────────────────────────────────────────
        //  Keys & header link
        // ────────────────────────────────────────────────────────────────
        [PXDBInt]
        [PXDBDefault(typeof(Series.bookSeriesID), PersistingCheck = PXPersistingCheck.Nothing)]
        [PXUIField(DisplayName = "Series ID")]
        [PXParent(typeof(SelectFrom<Series>
                         .Where<Series.bookSeriesID.IsEqual<SeriesDetail.seriesID.FromCurrent>>))]
        public int? SeriesID { get; set; }
        public abstract class seriesID : BqlInt.Field<seriesID> { }

        [PXDBIdentity(IsKey = true)]
        public int? SeriesRowID { get; set; }
        public abstract class seriesRowID : BqlInt.Field<seriesRowID> { }

        // ────────────────────────────────────────────────────────────────
        //  Book & scheduling
        // ────────────────────────────────────────────────────────────────
        [PXDBInt]
        [PXUIField(DisplayName = "Book ID")]
        [PXSelector(typeof(Search<InventoryItem.inventoryID,
                         Where<InventoryItem.itemStatus.IsNotEqual<InventoryItemStatus.inactive>>>),
                    typeof(InventoryItem.inventoryCD),
                    typeof(InventoryItem.descr),
                    SubstituteKey = typeof(InventoryItem.inventoryCD),
                    DescriptionField = typeof(InventoryItem.descr))]
        public int? Bookid { get; set; }
        public abstract class bookid : BqlInt.Field<bookid> { }

        [PXDBDate]
        [PXUIField(DisplayName = "Ship Date")]
        public DateTime? ShipDate { get; set; }
        public abstract class shipDate : BqlDateTime.Field<shipDate> { }

        // ────────────────────────────────────────────────────────────────
        //  Cycle lookup selectors
        // ────────────────────────────────────────────────────────────────
        [PXDBString(20)]
        [PXUIField(DisplayName = "Cycle Major")]
        [PXSelector(typeof(SelectFrom<CycleDetail>
                           .Where<CycleDetail.cycleID.IsEqual<Series.cycleID.FromCurrent>
                               .And<AccessInfo.businessDate.FromCurrent
                                   .Diff<CycleDetail.date>.Days.IsGreater<Zero>>>
                           .AggregateTo<
                               GroupBy<CycleDetail.cycleMajor>,
                               GroupBy<CycleDetail.sequenceMajor>,
                               Min<CycleDetail.date>>
                           .OrderBy<Asc<CycleDetail.sequenceMajor>>
                           .SearchFor<CycleDetail.cycleMajor>),
                    typeof(CycleDetail.sequenceMajor),
                    typeof(CycleDetail.cycleMajor),
                    typeof(CycleDetail.date),
                    SubstituteKey = typeof(CycleDetail.cycleMajor),
                    DescriptionField = typeof(CycleDetail.date))]
        public string CycleMajor { get; set; }
        public abstract class cycleMajor : BqlString.Field<cycleMajor> { }

        [PXDBString(50)]
        [PXUIField(DisplayName = "Cycle Minor")]
        [PXSelector(typeof(SelectFrom<CycleDetail>
                           .Where<CycleDetail.cycleID.IsEqual<Series.cycleID.FromCurrent>
                               .And<CycleDetail.cycleMajor.IsEqual<SeriesDetail.cycleMajor.FromCurrent>>>
                           .AggregateTo<
                               GroupBy<CycleDetail.cycleMinor>,
                               Min<CycleDetail.sequence>,
                               Min<CycleDetail.date>>
                           .OrderBy<Asc<CycleDetail.sequence>>
                           .SearchFor<CycleDetail.cycleMinor>),
                    typeof(CycleDetail.sequence),
                    typeof(CycleDetail.cycleMinor),
                    typeof(CycleDetail.date),
                    SubstituteKey = typeof(CycleDetail.cycleMinor),
                    DescriptionField = typeof(CycleDetail.date),
                    ValidateValue = false)]
        public string CycleMinor { get; set; }
        public abstract class cycleMinor : BqlString.Field<cycleMinor> { }

        // ────────────────────────────────────────────────────────────────
        //  Upcoming cycle values – computed by SQL via PXDBScalar
        // ────────────────────────────────────────────────────────────────
        [PXInt]
        [PXUIField(DisplayName = "Upcoming Cycle ID", Enabled = false)]
        public int? UpcomingCycleID { get; set; }
        public abstract class upcomingCycleID : BqlInt.Field<upcomingCycleID> { }

        [PXDBDate]
        [PXUIField(DisplayName = "Upcoming Cycle Date", Enabled = false)]
        public DateTime? UpcomingCycleDate { get; set; }
        public abstract class upcomingCycleDate : BqlDateTime.Field<upcomingCycleDate> { }
    }
}
