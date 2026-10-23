import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';

import { api, hasToken, setSessionLostHandler, setToken } from '../api/client';
import type { UserResponse } from '../api/types';
import { AuthContext, type AuthContextValue } from './authContext';

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserResponse | null>(null);

  // Starts true only when there is a token worth checking, so a first-time
  // visitor never sees a loading state before the sign-in form.
  const [loading, setLoading] = useState(hasToken());

  const signOut = useCallback(() => {
    setToken(null);
    setUser(null);
  }, []);

  useEffect(() => {
    setSessionLostHandler(() => setUser(null));
    return () => setSessionLostHandler(null);
  }, []);

  useEffect(() => {
    if (!hasToken()) {
      return;
    }

    // A stored token may be expired or revoked. Asking the API who we are is
    // the only way to find out, and it also refreshes the membership list.
    let cancelled = false;

    api
      .me()
      .then((me) => {
        if (!cancelled) {
          setUser(me);
        }
      })
      .catch(() => setToken(null))
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const signIn = useCallback(async (email: string, password: string) => {
    const result = await api.login({ email, password });
    setToken(result.accessToken);
    setUser(result.user);
  }, []);

  const signUp = useCallback(async (email: string, password: string, fullName: string) => {
    const result = await api.register({ email, password, fullName });
    setToken(result.accessToken);
    setUser(result.user);
  }, []);

  const refresh = useCallback(async () => {
    setUser(await api.me());
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({ user, loading, signIn, signUp, signOut, refresh }),
    [user, loading, signIn, signUp, signOut, refresh],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
