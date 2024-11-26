// -----------------------------------------------------------------------
// <copyright file="AlwaysRestartStrategy.cs" company="Asynkron AB">
//      Copyright (C) 2015-2024 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------

using System;

// ReSharper disable once CheckNamespace
namespace Proto;

/// <summary>
///     A supervision strategy that always restarts all of the children of the actor.
/// </summary>
public class AlwaysRestartStrategy : ISupervisorStrategy
{
    //always restart
    public void HandleFailure(
        ISupervisor supervisor,
        PID child,
        RestartStatistics rs,
        Exception reason,
        object? message
    ) =>
        supervisor.RestartChildren(reason, child);
}