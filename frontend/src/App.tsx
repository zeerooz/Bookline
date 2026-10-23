import { useCallback, useEffect, useState } from 'react';
import { BrowserRouter, Link, Navigate, Route, Routes, useLocation } from 'react-router-dom';

import { BookPage } from './pages/BookPage';
import { BusinessesPage } from './pages/BusinessesPage';
import { DashboardPage } from './pages/DashboardPage';
import { ManageBookingPage } from './pages/ManageBookingPage';
import { SignInPage } from './pages/SignInPage';
import { AuthProvider } from './state/AuthProvider';
import { useAuth } from './state/useAuth';

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Shell />
      </AuthProvider>
    </BrowserRouter>
  );
}

function Shell() {
  const [toast, setToast] = useState<string | null>(null);
  const notify = useCallback((message: string) => setToast(message), []);

  useEffect(() => {
    if (!toast) {
      return;
    }

    const timer = window.setTimeout(() => setToast(null), 2200);
    return () => window.clearTimeout(timer);
  }, [toast]);

  return (
    <>
      <TopBar />

      <Routes>
        {/* Public: no account, no chrome in the way of booking. */}
        <Route path="/book/:slug" element={<BookPage />} />
        <Route path="/booking/:slug/:token" element={<ManageBookingPage />} />

        <Route path="/signin" element={<SignInPage mode="in" />} />
        <Route path="/signup" element={<SignInPage mode="up" />} />

        <Route
          path="/app"
          element={
            <RequireAuth>
              <BusinessesPage />
            </RequireAuth>
          }
        />
        <Route
          path="/app/:slug"
          element={
            <RequireAuth>
              <DashboardPage notify={notify} />
            </RequireAuth>
          }
        />

        <Route path="*" element={<Landing />} />
      </Routes>

      {toast ? (
        <div className="toast" role="status">
          {toast}
        </div>
      ) : null}
    </>
  );
}

function TopBar() {
  const { user, signOut } = useAuth();
  const location = useLocation();

  // The public booking page belongs to the business, not to Bookline. Putting
  // our navigation on top of it would only distract from the one task.
  if (location.pathname.startsWith('/book/') || location.pathname.startsWith('/booking/')) {
    return null;
  }

  return (
    <header className="topbar">
      <div className="topbar-inner">
        <Link to="/" className="brand">
          Bookline
        </Link>

        {user ? (
          <nav className="topbar-nav">
            <Link to="/app" className="topbar-link">
              Businesses
            </Link>
          </nav>
        ) : null}

        <div className="topbar-right">
          {user ? (
            <>
              <span className="inline-note">{user.email}</span>
              <button type="button" className="btn-secondary" onClick={signOut}>
                Sign out
              </button>
            </>
          ) : (
            <>
              <Link to="/signin" className="topbar-link">
                Sign in
              </Link>
              <Link to="/signup" className="btn-primary" style={{ textDecoration: 'none' }}>
                Get started
              </Link>
            </>
          )}
        </div>
      </div>
    </header>
  );
}

function RequireAuth({ children }: { children: React.ReactNode }) {
  const { user, loading } = useAuth();

  if (loading) {
    return <p className="empty">Loading…</p>;
  }

  return user ? <>{children}</> : <Navigate to="/signin" replace />;
}

function Landing() {
  const { user } = useAuth();

  if (user) {
    return <Navigate to="/app" replace />;
  }

  return (
    <div className="page page-narrow stack-lg">
      <div>
        <h1>Take bookings without the back and forth</h1>
        <p className="page-sub">
          Publish your services and opening hours, share one link, and let customers pick a time that is actually
          free. Reminders go out automatically.
        </p>
      </div>

      <div className="row">
        <Link to="/signup" className="btn-primary" style={{ textDecoration: 'none' }}>
          Create your booking page
        </Link>
        <Link to="/signin" className="btn-secondary" style={{ textDecoration: 'none' }}>
          Sign in
        </Link>
      </div>

      <div className="grid">
        <div className="panel stack">
          <h3>Real availability</h3>
          <p className="panel-note">
            Slots come from your opening hours minus what is already booked, including clean-up time between
            appointments.
          </p>
        </div>
        <div className="panel stack">
          <h3>No double bookings</h3>
          <p className="panel-note">
            Two people clicking the same slot at the same moment is handled by the database, not by hoping.
          </p>
        </div>
        <div className="panel stack">
          <h3>Your team, your rules</h3>
          <p className="panel-note">
            Owners manage everything. Staff see only their own day. Customers need no account at all.
          </p>
        </div>
      </div>
    </div>
  );
}
