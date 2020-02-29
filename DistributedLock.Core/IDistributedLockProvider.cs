﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Medallion.Threading
{
    /// <summary>
    /// Acts as a factory for <see cref="IDistributedLock"/> instances of a certain type. This interface may be
    /// easier to use than <see cref="IDistributedLock"/> in dependency injection scenarios.
    /// </summary>
    public interface IDistributedLockProvider
    {
        IDistributedLock CreateLock(string name, bool exactName = false);

        /// <summary>
        /// Given an arbitrary <paramref name="name"/>, determines whether <paramref name="name"/> can be safely
        /// used as-is by the underlying provider (in other words, if it is safe to call <see cref="CreateLock(string, bool)"/>
        /// with exactName: true).
        /// 
        /// If <paramref name="name"/> is safe to use, returns <paramref name="name"/>. Otherwise, returns a new name which
        /// is safe to use and incorporates as much of the uniqueness of <paramref name="name"/> as possible. For example, this
        /// may be a hash of <paramref name="name"/> that fits within the length and character requirements for the underlying
        /// locking mechanism.
        /// </summary>
        string GetSafeLockName(string name);
    }
}
