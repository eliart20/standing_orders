using System;
using PX.Data;
using PX.Data.BQL;
using PX.Data.BQL.Fluent;
using PX.Objects.IN;

namespace StandingOrders
{
    /// <summary>
    /// Projection = SeriesDetail  +  Series (for header filters)  +
    ///              “next” CycleDetail (same Major/Minor, not passed, same CycleID).
    /// </summary>
    [PXProjection(
        typeof(
            SelectFrom<SeriesDetail>
            .InnerJoin<Series>
                .On<SeriesDetail.seriesID.IsEqual<Series.bookSeriesID>>
            .LeftJoin<CycleDetail>
                .On<CycleDetail.cycleMajor.IsEqual<SeriesDetail.cycleMajor>
                 .And<CycleDetail.cycleMinor.IsEqual<SeriesDetail.cycleMinor>>
                 .And<CycleDetail.isPast.IsEqual<False>>
                 .And<CycleDetail.cycleID.IsEqual<Series.cycleID>>>
            .AggregateTo<
                /* one record per detail line; keep other columns with MAX() */
                GroupBy<SeriesDetail.seriesRowID>,
                Max<SeriesDetail.seriesID>,
                Max<SeriesDetail.bookid>,
                Max<SeriesDetail.cycleMajor>,
                Max<SeriesDetail.cycleMinor>,
                Max<Series.cycleID>,         // Add this line to include Series.cycleID
                Min<CycleDetail.date>,       // earliest upcoming cycle
                Min<CycleDetail.cycleID>     // its ID
            >
        ),
        Persistent = true)]
    [Serializable]
    public sealed class SeriesDetailProj : PXBqlTable, IBqlTable
    {
        //─────────────────────────────────────
        // Keys (SeriesDetail identity)
        //─────────────────────────────────────
        [PXDBIdentity(IsKey = true, BqlField = typeof(SeriesDetail.seriesRowID))]
        public int? SeriesRowID { get; set; }
        public abstract class seriesRowID : BqlInt.Field<seriesRowID> { }

        [PXDBInt(BqlField = typeof(SeriesDetail.seriesID))]
        [PXDBDefault(typeof(Series.bookSeriesID))]   // ← sets the value automatically
        [PXParent(typeof(SelectFrom<Series>           // optional but lets Acumatica
           .Where<Series.bookSeriesID.IsEqual<SeriesDetailProj.seriesID>>))] // cascade delete
        [PXUIField(DisplayName = "Series ID", Enabled = false)]
        public int? SeriesID { get; set; }
        public abstract class seriesID : BqlInt.Field<seriesID> { }

        [PXDBInt(BqlField = typeof(Series.cycleID))]
        [PXUIField(DisplayName = "Cycle ID", Enabled = false, Visible = false)]
        public int? CycleID { get; set; }
        public abstract class cycleID : BqlInt.Field<cycleID> { }

        //─────────────────────────────────────
        // Book  (editable, persists to SeriesDetail)
        //─────────────────────────────────────
        [PXDBInt(BqlField = typeof(SeriesDetail.bookid))]
        [PXUIField(DisplayName = "Book ID")]
        [PXSelector(typeof(Search<InventoryItem.inventoryID,
                         Where<InventoryItem.itemStatus.IsNotEqual<InventoryItemStatus.inactive>>>),
                    typeof(InventoryItem.inventoryCD),
                    typeof(InventoryItem.descr),
                    SubstituteKey = typeof(InventoryItem.inventoryCD),
                    DescriptionField = typeof(InventoryItem.descr))]
        public int? Bookid { get; set; }
        public abstract class bookid : BqlInt.Field<bookid> { }

        // Fixed CycleMajor selector - now using the local CycleID
        [PXDBString(20, BqlField = typeof(SeriesDetail.cycleMajor))]
        [PXUIField(DisplayName = "Cycle Major")]
        [PXSelector(
            typeof(SelectFrom<CycleDetail>
                   .Where<CycleDetail.cycleID.IsEqual<SeriesDetailProj.cycleID.FromCurrent>
                       .And<CycleDetail.date.IsGreater<AccessInfo.businessDate.FromCurrent>>>
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

        // Fixed CycleMinor selector
        [PXDBString(50, BqlField = typeof(SeriesDetail.cycleMinor))]
        [PXUIField(DisplayName = "Cycle Minor")]
        [PXSelector(
            typeof(SelectFrom<CycleDetail>
                   .Where<CycleDetail.cycleID.IsEqual<SeriesDetailProj.cycleID.FromCurrent>
                       .And<CycleDetail.cycleMajor.IsEqual<SeriesDetailProj.cycleMajor.FromCurrent>>
                       .And<CycleDetail.date.IsGreater<AccessInfo.businessDate.FromCurrent>>>
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



        [PXDBDate(BqlField = typeof(SeriesDetail.shipDate))]
        [PXUIField(DisplayName = "Ship Date")]
        public DateTime? ShipDate { get; set; }
        public abstract class shipDate : BqlDateTime.Field<shipDate> { }

        //─────────────────────────────────────
        // Joined / aggregated read-only columns
        //─────────────────────────────────────
        [PXDBInt(BqlField = typeof(CycleDetail.cycleID))]
        [PXUIField(DisplayName = "Upcoming Cycle ID", Enabled = false)]
        public int? UpcomingCycleID { get; set; }
        public abstract class upcomingCycleID : BqlInt.Field<upcomingCycleID> { }

        [PXDBDate(BqlField = typeof(CycleDetail.date))]
        [PXUIField(DisplayName = "Upcoming Cycle Date", Enabled = false)]
        public DateTime? UpcomingCycleDate { get; set; }
        public abstract class upcomingCycleDate : BqlDateTime.Field<upcomingCycleDate> { }

    }
}
