using System.Collections;
using System.Linq;
using PX.Data;
using StandingOrders;

namespace PX.Objects.SO
{
    // Acuminator disable once PX1016 ExtensionDoesNotDeclareIsActiveMethod extension should be constantly active
    public class SOOrderEntry_Extension : PXGraphExtension<PX.Objects.SO.SOOrderEntry>
  {
        #region Event Handlers

        public PXAction<SOOrder> AddSeriesItems;





        [PXButton(CommitChanges = true)]
        [PXUIField(DisplayName = "Add Series Items", Enabled = true)] //, Visibility = PXUIVisibility.V)]
        [PXUIEnabled(typeof(Where<SOOrder.customerID, IsNotNull,
                                                And<SOOrder.customerLocationID, IsNotNull>>))]
        protected IEnumerable addSeriesItems(PXAdapter adapter)
        {
            PXTrace.WriteInformation("AddSeriesItems button clicked.");

            SOOrder order = Base.Document.Current;
            if (order == null) return adapter.Get();

            SOOrderExt orderExt = order.GetExtension<SOOrderExt>();
            if (orderExt?.UsrBookSeriesCD == null)
            {
                const string SelectBookSeriesMessage = "Please select a Book Series before adding items.";
                throw new PXException(SelectBookSeriesMessage);
            }

            Series series = PXSelect<
                                Series,
                                Where<Series.bookSeriesCD, Equal<Required<Series.bookSeriesCD>>>>
                             .Select(Base, orderExt.UsrBookSeriesCD);
            if (series == null)
            {
                const string SeriesNotFoundMessage = "Series not found for the selected Book Series.";
                throw new PXException(SeriesNotFoundMessage);
            }

            PXResultset<SeriesDetail> details =
                PXSelect<
                    SeriesDetail,
                    Where<SeriesDetail.seriesID, Equal<Required<SeriesDetail.seriesID>>>, OrderBy<Asc<SeriesDetail.shipDate>>>
                .Select(Base, series.BookSeriesID) ;


            foreach (SeriesDetail det in details)
            {
                if (det?.Bookid == null) continue;

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

            return adapter.Get();      
        }

        protected virtual void _(Events.RowSelected<SOOrder> e)
        {
            if (e.Row == null) return;

            bool isST = e.Row.OrderType == "ST";         
            AddSeriesItems.SetVisible(isST);            
            setAddSeriesEnabled(e);
            PXTrace.WriteInformation($"Order Type: {e.Row.OrderType}, Is ST: {isST}");
        }

        protected void setAddSeriesEnabled(Events.RowSelected<SOOrder> e)
        {
            var customer  = e.Row?.CustomerID;
            var location = e.Row?.CustomerLocationID;
            var isST = e.Row?.OrderType == "ST"; 
            if( customer == null || location == null || !isST)
            {
                AddSeriesItems.SetEnabled(false);
            }
            else
            {
                AddSeriesItems.SetEnabled(true);
            }

        }




        #endregion
    }
}