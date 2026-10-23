import { createContext } from 'react';

import type { UserResponse } from '../api/types';

export interface AuthContextValue {
  user: UserResponse | null;
  loading: boolean;
  signIn: (email: string, password: string) => Promise<void>;
  signUp: (email: string, password: string, fullName: string) => Promise<void>;
  signOut: () => void;
  refresh: () => Promise<void>;
}

// Kept apart from the provider component so that module exports only
// components, which is what React Fast Refresh needs.
export const AuthContext = createContext<AuthContextValue | null>(null);
