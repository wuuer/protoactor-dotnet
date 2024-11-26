// -----------------------------------------------------------------------
// <copyright file="ProtoLabels.cs" company="Asynkron AB">
//      Copyright (C) 2015-2024 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------

namespace Proto.Cluster.Kubernetes;

public static class ProtoLabels
{
    public const string ProtoClusterPrefix = "cluster.proto.actor/";
    public const string LabelPort = ProtoClusterPrefix + "port";
    public const string LabelCluster = ProtoClusterPrefix + "cluster";
    public const string LabelMemberId = ProtoClusterPrefix + "member-id";
    public const string AnnotationKinds = ProtoClusterPrefix + "kinds";
    public const string LabelHost = ProtoClusterPrefix + "host";
    public const string LabelHostPrefix = ProtoClusterPrefix + "host-prefix";
}