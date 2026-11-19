package com.orzkwsk.colmiringvrcbridge;

import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;

import java.net.Inet4Address;
import java.net.InetAddress;

final class WifiIpv4 {
    private WifiIpv4() {}

    @SuppressWarnings("deprecation") // Read once on tap, including a Wi-Fi LAN without internet.
    static String currentPrefix(ConnectivityManager manager) {
        if (manager == null) {
            return null;
        }

        Network active = manager.getActiveNetwork();
        String prefix = fromWifi(manager, active);
        if (prefix != null) {
            return prefix;
        }

        for (Network network : manager.getAllNetworks()) {
            prefix = fromWifi(manager, network);
            if (prefix != null) {
                return prefix;
            }
        }
        return null;
    }

    private static String fromWifi(ConnectivityManager manager, Network network) {
        if (network == null) {
            return null;
        }
        NetworkCapabilities capabilities = manager.getNetworkCapabilities(network);
        if (capabilities == null || !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
                || capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
            return null;
        }
        LinkProperties link = manager.getLinkProperties(network);
        if (link != null) {
            for (LinkAddress linkAddress : link.getLinkAddresses()) {
                String prefix = prefixOf(linkAddress.getAddress());
                if (prefix != null) {
                    return prefix;
                }
            }
        }
        return null;
    }

    static String prefixOf(InetAddress address) {
        if (!(address instanceof Inet4Address) || address.isLoopbackAddress()
                || address.isAnyLocalAddress() || address.isLinkLocalAddress()) {
            return null;
        }
        byte[] bytes = address.getAddress();
        return Byte.toUnsignedInt(bytes[0]) + "." + Byte.toUnsignedInt(bytes[1]) + "."
                + Byte.toUnsignedInt(bytes[2]) + ".";
    }
}
