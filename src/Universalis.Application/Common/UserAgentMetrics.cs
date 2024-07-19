﻿using Prometheus;
using System.Diagnostics;
using UAParser;

namespace Universalis.Application.Common;

public class UserAgentMetrics
{
    protected static readonly Counter UserAgentRequestCount =
        Metrics.CreateCounter("universalis_request_count_user_agents", "", "Controller", "Family");

    public static void RecordUserAgentRequest(string userAgent, string controllerName, Activity activity = null)
    {
        // For some reason user agents replace spaces with pluses sometimes - convert it back
        userAgent = userAgent.Replace('+', ' ');
        
        var controllerMetricName = GetControllerMetricName(controllerName);
        if (!string.IsNullOrEmpty(userAgent))
        {
            var parsedUserAgent = Parser.GetDefault().ParseUserAgent(userAgent);
            var userAgentFamily = parsedUserAgent.Family;
            if (userAgentFamily == "Other")
            {
                var firstSpace = userAgent.IndexOf(' ');
                var inferredUserAgentFriendlyName = firstSpace != -1 ? userAgent[..(firstSpace + 1)] : userAgent;
                activity?.AddTag("userAgent", inferredUserAgentFriendlyName);
                UserAgentRequestCount.Labels(controllerMetricName, inferredUserAgentFriendlyName).Inc();
            }
            else
            {
                activity?.AddTag("userAgent", userAgentFamily);
                UserAgentRequestCount.Labels(controllerMetricName, userAgentFamily).Inc();
            }
        }
        else
        {
            activity?.AddTag("userAgent", "(no user agent)");
            UserAgentRequestCount.Labels(controllerMetricName, "(no user agent)").Inc();
        }
    }

    private static string GetControllerMetricName(string controllerName)
    {
        const string suffix = "Controller";
        if (controllerName.EndsWith(suffix))
        {
            return controllerName[..^suffix.Length];
        }

        return controllerName;
    }
}
