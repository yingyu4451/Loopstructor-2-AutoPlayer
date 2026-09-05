using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Loopstructor.QA.EditorBridge
{
    internal static class QaCatalogReader
    {
        /// <summary>读取 Edit Mode 可用的基础枚举目录。</summary>
        public static JObject Read()
        {
            JArray disposables = ReadEnum("MetroTD.DisposableSystem.DisposableEnum");
            return new JObject
            {
                ["success"] = true,
                ["vehicles"] = ReadEnum("MetroTD.VehicleSystem.VehicleType"),
                ["enchantments"] = ReadEnum("FetterEnum"),
                ["disposables"] = FilterDisposables(disposables),
                ["catapultPoints"] = FilterCatapultPoints(disposables),
                ["relics"] = ReadEnum("MetroTD.SuperModuleSystem.SuperModuleEnum")
            };
        }

        /// <summary>按完整类型名读取枚举值。</summary>
        private static JArray ReadEnum(string typeName)
        {
            Type type = FindType(typeName);
            JArray result = new JArray();
            if (type == null || !type.IsEnum) return result;
            Array values = Enum.GetValues(type);
            HashSet<long> seen = new HashSet<long>();
            foreach (object value in values)
            {
                string name = value.ToString();
                long numeric = Convert.ToInt64(value);
                if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase) || !seen.Add(numeric)) continue;
                result.Add(new JObject { ["id"] = name, ["enumName"] = name, ["name"] = name });
            }
            return result;
        }

        /// <summary>从道具枚举中提取可识别的弹射点条目。</summary>
        private static JArray FilterCatapultPoints(JArray source)
        {
            JArray result = new JArray();
            foreach (JObject item in source.Children<JObject>())
            {
                string name = item.Value<string>("id") ?? string.Empty;
                if (name.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Catapult", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("Station", StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(item.DeepClone());
            }
            return result;
        }

        /// <summary>从道具枚举中排除弹射点条目。</summary>
        private static JArray FilterDisposables(JArray source)
        {
            HashSet<string> catapultIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject item in FilterCatapultPoints(source).Children<JObject>())
                catapultIds.Add(item.Value<string>("id") ?? string.Empty);
            JArray result = new JArray();
            foreach (JObject item in source.Children<JObject>())
            {
                if (!catapultIds.Contains(item.Value<string>("id") ?? string.Empty)) result.Add(item.DeepClone());
            }
            return result;
        }

        /// <summary>在当前 AppDomain 查找游戏类型。</summary>
        private static Type FindType(string fullName)
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
