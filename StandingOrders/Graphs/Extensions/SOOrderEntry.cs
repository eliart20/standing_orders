using System.Collections;
using PX.Data;
using StandingOrders;

namespace PX.Objects.SO
{
    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public class SOOrderEntry_Extension : PXGraphExtension<PX.Objects.SO.SOOrderEntry>
    {
        #region Event Handlers

        /// <summary>
        /// Fire whenever the Book Series field changes.
        /// Clears existing lines and adds the series items.
        /// </summary>
        protected virtual void _(Events.FieldUpdated<SOOrder, SOOrderExt.usrBookSeriesCD> e)
        {
            if (e.NewValue == null)                                           // ignore clears
                return;

            // remove any existing lines (if present)
            foreach (SOLine line in Base.Transactions.Select())
                Base.Transactions.Delete(line);

            AddSeriesItems((int?)e.NewValue);
        }

        #endregion

        #region Helpers

        private void AddSeriesItems(int? bookSeriesCD)
        {
            Series series = PXSelect<
                                Series,
                                Where<Series.bookSeriesCD, Equal<Required<Series.bookSeriesCD>>>>
                            .Select(Base, bookSeriesCD);

            if (series == null)
                throw new PXException("Series not found for the selected Book Series.");
            PXResultset<SeriesDetail> details = PXSelect<SeriesDetail,
                Where<SeriesDetail.seriesID, Equal<Required<SeriesDetail.seriesID>>,
                    And<SeriesDetail.shipDate, Greater<Required<SeriesDetail.shipDate>>>>,
                OrderBy<Asc<SeriesDetail.shipDate>>>
            .Select(Base, series.BookSeriesID, Base.Accessinfo.BusinessDate);

            foreach (SeriesDetail det in details)
            {
                if (det?.Bookid == null)
                    continue;

                SOLine line = new SOLine
                {
                    InventoryID = det.Bookid,
                    OrderQty = 1m,
                    SchedOrderDate = det.ShipDate,
                    SchedShipDate = det.ShipDate,
                    ShipComplete = SOShipComplete.BackOrderAllowed
                };

                Base.Transactions.Insert(line);
            }
        }

        #endregion
    }
}
