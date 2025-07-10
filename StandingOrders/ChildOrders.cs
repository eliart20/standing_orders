using PX.Common;
using PX.Data;
using PX.Objects.CS;
using PX.Objects.SO;
using StandingOrders;
using System;

public static class ShipmentHelper
{
    public static SOShipment CreateShipmentForItem(
    PXGraph graph,
    string orderType,
    string orderNbr,
    int inventoryID,
    int? siteID = null,
    DateTime? shipDate = null)
    {
        /* 1. Load the order and the line ------------------------------- */
        SOOrder so = PXSelect<
                        SOOrder,
                        Where<SOOrder.orderType, Equal<Required<SOOrder.orderType>>,
                        And<SOOrder.orderNbr, Equal<Required<SOOrder.orderNbr>>>>>
                     .Select(graph, orderType, orderNbr)
                     .TopFirst
                     ?? throw new PXException(STMessages.OrderNotFound,
                                              orderType, orderNbr);

        SOLine line = PXSelect<
                        SOLine,
                        Where<SOLine.orderType, Equal<Required<SOLine.orderType>>,
                        And<SOLine.orderNbr, Equal<Required<SOLine.orderNbr>>,
                        And<SOLine.inventoryID, Equal<Required<SOLine.inventoryID>>,
                        And<SOLine.openQty, Greater<decimal0>>>>>>
                     .Select(graph, orderType, orderNbr, inventoryID)
                     .TopFirst
                     ?? throw new PXException(STMessages.NoOpenQtyForItem,
                                              orderType, orderNbr, inventoryID);

        int? effectiveSite = siteID ?? line.SiteID
            ?? throw new PXException(STMessages.SiteCannotBeBlank);

        /* 2. Spin up a shipment entry graph ---------------------------- */
        var se = PXGraph.CreateInstance<SOShipmentEntry>();

        /* 3. Create an empty header — let Acumatica default everything  */
        var header = new SOShipment
        {
            //Operation = so.Operation,             // 'I', 'R', or 'T'
            CustomerID = so.CustomerID,
            SiteID = effectiveSite,
            ShipDate = shipDate
                         ?? graph.Accessinfo.BusinessDate.GetValueOrDefault()
        };
        header = se.Document.Insert(header);       // defaults ShipVia, address, etc.

        /* 4. Use the built‑in Add Order dialog ------------------------- */
        se.addsofilter.Current.OrderType = so.OrderType;
        se.addsofilter.Current.OrderNbr = so.OrderNbr;
        //se.addsofilter.Current.Operation = so.Operation;
        se.addsofilter.Update(se.addsofilter.Current);

        foreach (PXResult<SOShipmentPlan, SOLineSplit, SOLine> plan
                 in se.soshipmentplan.Select())
        {
            var planLine = (SOShipmentPlan)plan;
            var planSplit = (SOLineSplit)plan;

            // keep ONLY the requested inventory ID
            planLine.Selected = planSplit.InventoryID == inventoryID;
            se.soshipmentplan.Update(planLine);
        }

        se.addSO.Press();                          // populates SOShipLine correctly

        if (se.Transactions.Current == null)
            throw new PXException(STMessages.NoOpenQtyForItem,
                                  orderType, orderNbr, inventoryID);

        /* 5. Save and return ------------------------------------------ */
        se.Save.Press();
        return se.Document.Current;
    }

}
