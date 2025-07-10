using System;
using PX.Data;

namespace StandingOrders
{
  [Serializable]
  [PXCacheName("CycleDetail")]
  public class CycleDetail : PXBqlTable, IBqlTable
  {
    #region CycleDetailID
    [PXDBIdentity(IsKey = true)]
    public virtual int? CycleDetailID { get; set; }
    public abstract class cycleDetailID : PX.Data.BQL.BqlInt.Field<cycleDetailID> { }
    #endregion

    #region CycleID
    [PXDBInt()]
    [PXUIField(DisplayName = "Cycle ID")]
    public virtual int? CycleID { get; set; }
    public abstract class cycleID : PX.Data.BQL.BqlInt.Field<cycleID> { }
    #endregion

    #region CycleMajor
    [PXDBString(20, InputMask = "")]
    [PXUIField(DisplayName = "Cycle Major")]
    public virtual string CycleMajor { get; set; }
    public abstract class cycleMajor : PX.Data.BQL.BqlString.Field<cycleMajor> { }
    #endregion

    #region CycleMinor
    [PXDBString(50, InputMask = "")]
    [PXUIField(DisplayName = "Cycle Minor")]
    public virtual string CycleMinor { get; set; }
    public abstract class cycleMinor : PX.Data.BQL.BqlString.Field<cycleMinor> { }
        #endregion

    #region Sequence
    [PXDBInt]
    [PXUIField(DisplayName = "Sequence")]
    public virtual int? Sequence { get; set; }
    public abstract class sequence : PX.Data.BQL.BqlInt.Field<sequence> { }
    #endregion

    #region SequenceMajor
    [PXDBInt]
    [PXUIField(DisplayName = "Sequence Major")]
    public virtual int? SequenceMajor { get; set; }
    public abstract class sequenceMajor : PX.Data.BQL.BqlInt.Field<sequenceMajor> { }
    #endregion



    #region Date
    [PXDBDate()]
    [PXUIField(DisplayName = "Date")]
    public virtual DateTime? Date { get; set; }
    public abstract class date : PX.Data.BQL.BqlDateTime.Field<date> { }
        #endregion

    [PXDBBool(BqlField = typeof(CycleDetail.isPast))]          // maps to the DB column
    [PXUIField(DisplayName = "Is Past", Enabled = false)]
    public bool? IsPast { get; set; }
    public abstract class isPast :
        PX.Data.BQL.BqlBool.Field<isPast>
    { }
    }
}