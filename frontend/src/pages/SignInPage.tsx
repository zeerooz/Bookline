import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';

import { useAuth } from '../state/useAuth';

export function SignInPage({ mode }: { mode: 'in' | 'up' }) {
  const { signIn, signUp } = useAuth();
  const navigate = useNavigate();

  const [fullName, setFullName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isSignUp = mode === 'up';

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      if (isSignUp) {
        await signUp(email.trim(), password, fullName.trim());
      } else {
        await signIn(email.trim(), password);
      }

      navigate('/app');
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Something went wrong.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="page page-narrow stack-lg">
      <div>
        <h1>{isSignUp ? 'Create an account' : 'Sign in'}</h1>
        <p className="page-sub">
          {isSignUp
            ? 'For businesses taking bookings. Customers do not need an account.'
            : 'Manage your calendar, services and team.'}
        </p>
      </div>

      <form className="stack" onSubmit={submit} style={{ maxWidth: 380 }}>
        {isSignUp ? (
          <label className="field">
            <span>Your name</span>
            <input type="text" value={fullName} onChange={(e) => setFullName(e.target.value)} required autoFocus />
          </label>
        ) : null}

        <label className="field">
          <span>Email</span>
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="username"
            required
            autoFocus={!isSignUp}
          />
        </label>

        <label className="field">
          <span>Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={isSignUp ? 'new-password' : 'current-password'}
            required
          />
          {isSignUp ? <span className="field-hint">At least 10 characters.</span> : null}
        </label>

        {error ? <p className="notice notice-error">{error}</p> : null}

        <div className="row">
          <button type="submit" className="btn-primary" disabled={busy}>
            {busy ? 'Working…' : isSignUp ? 'Create account' : 'Sign in'}
          </button>
        </div>

        <p className="field-hint">
          {isSignUp ? (
            <>
              Already have an account? <Link to="/signin">Sign in</Link>
            </>
          ) : (
            <>
              New here? <Link to="/signup">Create an account</Link>
            </>
          )}
        </p>
      </form>
    </div>
  );
}
