using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace NServiceBus.SqlTransport.Tests.Shared
{
    public class CommandLineOptions
    {
        public Dictionary<string, List<string>> Options { get; set; }
        public string[] Commands { get; set; }

        public string TryGetSingle(string option)
        {
            if (Options.TryGetValue(option, out var values))
            {
                if (values.Count > 1)
                {
                    throw new Exception($"Expected single value for option {option} but got ${string.Join(",", values)}");
                }
                return values[0];
            }
            return null;
        }

        public T TryGetSingle<T>(string option)
        {
            if (Options.TryGetValue(option, out var values))
            {
                if (values.Count > 1)
                {
                    throw new Exception($"Expected single value for option {option} but got ${string.Join(",", values)}");
                }
                var actualType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T) Convert.ChangeType(values[0], actualType);
            }
            return default;
        }

        public string[] TryGetMany(string option)
        {
            if (Options.TryGetValue(option, out var values))
            {
                return values.ToArray();
            }
            return null;
        }

        public T[] TryGetMany<T>(string option, int? minCount = null, int? maxCount = null)
        {
            if (Options.TryGetValue(option, out var values))
            {
                var result = values.Select(v => (T) Convert.ChangeType(values[0], typeof(T))).ToArray();
                if (minCount.HasValue && result.Length < minCount)
                {
                    throw new Exception($"Expected at least {minCount} values for argument {option}.");
                }
                if (maxCount.HasValue && result.Length > maxCount)
                {
                    throw new Exception($"Expected at most {maxCount} values for argument {option}.");
                }

                return result;
            }
            return null;
        }

        public static CommandLineOptions Parse(string[] args)
        {
            var commands = new List<string>();
            string currentOption = null;
            var options = new Dictionary<string, List<string>>();
            foreach (var arg in args)
            {
                if (arg.StartsWith("-", StringComparison.OrdinalIgnoreCase))
                {
                    currentOption = arg.Substring(1);
                    options[currentOption] = new List<string>();
                }
                else
                {
                    if (currentOption == null)
                    {
                        commands.Add(arg);
                    }
                    else
                    {
                        options[currentOption].Add(arg);
                    }
                }
            }

            return new CommandLineOptions
            {
                Commands = commands.ToArray(),
                Options = options
            };
        }
    }
}