using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DLAT.JTReader {
    public class DatePropertyAtomData : BasePropertyAtomData {
        public DateTime date;
        public DatePropertyAtomData(Element ele) : base(ele) {
            var data = ele.dataStream;
            var version = 0;
            if(ele.majorVersion > 8)
                version = ele.majorVersion > 9 ? data.ReadU8() : data.ReadI16();
            //read y,m,d,h,m,s all i16 and then convert to DateTime
            var year = data.ReadI16();
            var month = data.ReadI16();
            var day = data.ReadI16();
            var hour = data.ReadI16();
            var minute = data.ReadI16();
            var second = data.ReadI16();
            date = new DateTime(year, month, day, hour, minute, second);
        }
        
        public override string ToString() {
            return "date: " + date.ToString();
        }
        
        //重载DateTime 类型
        public static implicit operator DateTime(DatePropertyAtomData data) {
            return data.date;
        }
    }
}
