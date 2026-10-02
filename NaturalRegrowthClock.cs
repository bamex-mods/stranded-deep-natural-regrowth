using System;
using System.Reflection;

namespace StrandedDeepNaturalRegrowth
{
    // Production clock: always re-read Beam.GameTime.Now live.
    internal sealed class NaturalRegrowthClock
    {
        private string _source = "unresolved";

        public string Source { get { return _source; } }

        public bool TryGetGameDay(out double gameDay)
        {
            gameDay = 0.0;
            try
            {
                Type gameTimeType = typeof(StrandedWorld).Assembly.GetType("Beam.GameTime");
                if (gameTimeType == null)
                {
                    _source = "Beam.GameTime type not found";
                    return false;
                }

                PropertyInfo now = gameTimeType.GetProperty("Now", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (now == null || now.GetIndexParameters().Length != 0)
                {
                    _source = "Beam.GameTime.Now property not found";
                    return false;
                }

                object value = now.GetValue(null, null);
                if (!(value is DateTime))
                {
                    _source = "Beam.GameTime.Now returned " + (value == null ? "null" : value.GetType().FullName);
                    return false;
                }

                DateTime dt = (DateTime)value;
                gameDay = dt.Ticks / (double)TimeSpan.TicksPerDay;
                _source = "Beam.GameTime.Now [live getter]";
                return true;
            }
            catch (Exception ex)
            {
                _source = "Beam.GameTime.Now error: " + ex.GetType().Name;
                return false;
            }
        }
    }
}
