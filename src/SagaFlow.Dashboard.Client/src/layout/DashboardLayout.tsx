import {
  Boxes,
  GitBranch,
  PanelLeftOpen,
  type LucideIcon,
} from "lucide-react";
import { useMemo, useState, type ReactNode } from "react";

import { SidebarItem } from "../components/Sidebar/SidebarItem";
import { ThemeToggle } from "../components/ThemeToggle/ThemeToggle";
import { DefinitionsPage } from "../pages/DefinitionsPage";
import { InstancesPage } from "../pages/InstancesPage";

const pages = [
  "Workflows",
  "Instances",
] as const;

type Page = (typeof pages)[number];

const navigationIcons: Record<Page, LucideIcon> = {
  Workflows: GitBranch,
  Instances: Boxes,
};

const breadcrumbs: Record<Page, string[]> = {
  Workflows: ["Dashboard", "Workflows"],
  Instances: ["Dashboard", "Saga Instances"],
};

export function DashboardLayout() {
  const [activePage, setActivePage] = useState<Page>("Workflows");
  const [isSidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [isMobileSidebarOpen, setMobileSidebarOpen] = useState(false);

  const content = useMemo(() => {
    switch (activePage) {
      case "Workflows":
        return <DefinitionsPage />;
      case "Instances":
        return <InstancesPage />;
    }
  }, [activePage]);

  const closeMobileSidebar = () => {
    setMobileSidebarOpen(false);
  };

  const navigateTo = (page: Page) => {
    setActivePage(page);
    closeMobileSidebar();
  };

  return (
    <main className="flex h-dvh w-full overflow-hidden bg-zinc-50 text-zinc-950 dark:bg-[#050505] dark:text-zinc-100">
      {isMobileSidebarOpen && (
        <button
          type="button"
          aria-label="Close navigation backdrop"
          className="fixed inset-0 z-30 bg-black/60 backdrop-blur-sm lg:hidden"
          onClick={closeMobileSidebar}
        />
      )}

      <aside
        className={[
          "fixed inset-y-0 left-0 z-40 flex h-dvh flex-col border-r border-zinc-200 bg-white p-5 shadow-2xl transition-all duration-200 dark:border-zinc-800 dark:bg-[#070707] lg:static lg:z-auto lg:shadow-none",
          isMobileSidebarOpen
            ? "translate-x-0"
            : "-translate-x-full lg:translate-x-0",
          isSidebarCollapsed
            ? "static z-auto w-20 translate-x-0 px-3 shadow-none"
            : "w-[320px] max-w-[calc(100vw-48px)] lg:w-[270px]",
        ].join(" ")}
      >
        <button
          type="button"
          aria-label="Go to dashboard home"
          title="Dashboard home"
          onClick={() => navigateTo("Workflows")}
          className={[
            "flex min-h-10 items-center gap-3 rounded-lg text-left transition focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-emerald-500",
            isSidebarCollapsed ? "justify-center" : "",
          ].join(" ")}
        >
          <span className="inline-flex size-11 shrink-0 items-center justify-center rounded-2xl bg-emerald-500 text-lg font-bold text-white shadow-sm shadow-emerald-500/20">
            SF
          </span>
          <span className={isSidebarCollapsed ? "hidden" : "min-w-0"}>
            <span className="block truncate text-lg font-bold tracking-tight text-zinc-950 dark:text-zinc-100">
              SagaFlow
            </span>
            <span className="block truncate text-xs font-medium text-zinc-500 dark:text-zinc-500">
              Embedded Dashboard
            </span>
          </span>
        </button>

        <div className={isSidebarCollapsed ? "mt-9 lg:mt-10" : "mt-9"}>
          <nav className="flex flex-col gap-1">
            {pages.map((page) => (
              <SidebarItem
                key={page}
                label={page}
                active={activePage === page}
                collapsed={isSidebarCollapsed}
                icon={navigationIcons[page]}
                onClick={() => navigateTo(page)}
              />
            ))}
          </nav>
        </div>

        <div className="mt-auto">
          <div className={isSidebarCollapsed ? "flex justify-center" : "flex justify-end"}>
            <button
              type="button"
              className="inline-flex size-10 shrink-0 items-center justify-center rounded-xl border border-zinc-200 bg-white text-zinc-700 shadow-sm transition hover:bg-zinc-100 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-100 dark:hover:border-zinc-700 dark:hover:bg-zinc-900"
              aria-label={
                isSidebarCollapsed ? "Expand navigation" : "Collapse navigation"
              }
              aria-expanded={!isSidebarCollapsed}
              onClick={() => {
                if (isSidebarCollapsed) {
                  setSidebarCollapsed(false);
                  setMobileSidebarOpen(
                    window.matchMedia("(max-width: 1023px)").matches,
                  );
                  return;
                }

                setMobileSidebarOpen(false);
                setSidebarCollapsed(true);
              }}
            >
              <SidebarCollapseIcon
                direction={isSidebarCollapsed ? "right" : "left"}
              />
            </button>
          </div>
        </div>
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-[88px] shrink-0 items-center justify-between gap-4 border-b border-zinc-200 bg-white px-6 dark:border-zinc-800 dark:bg-[#050505] lg:px-10">
          <div className="flex min-w-0 items-center gap-4">
            <button
              type="button"
              className="inline-flex size-10 items-center justify-center rounded-xl border border-zinc-200 bg-white text-zinc-700 transition hover:border-zinc-300 hover:bg-zinc-100 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-100 dark:hover:border-zinc-700 dark:hover:bg-zinc-900 lg:hidden"
              aria-label="Open navigation"
              aria-expanded={isMobileSidebarOpen}
              onClick={() => setMobileSidebarOpen(true)}
            >
              <PanelLeftOpen size={19} strokeWidth={2} aria-hidden="true" />
            </button>

            <div className="min-w-0">
              <h1 className="m-0 truncate text-[22px] font-bold leading-7 tracking-tight text-zinc-950 dark:text-zinc-100">
                {activePage}
              </h1>

              <Breadcrumb items={breadcrumbs[activePage]} />
            </div>
          </div>

          <div className="flex shrink-0 items-center gap-2">
            <ThemeToggle />
          </div>
        </header>

        <div id="dashboard-content" className="min-h-0 flex-1 overflow-auto p-6 lg:p-10">
          {content}
        </div>
      </section>
    </main>
  );
}

function Breadcrumb({ items }: { items: string[] }) {
  return (
    <nav
      aria-label="Breadcrumb"
      className="mt-1 flex h-5 min-w-0 items-center gap-1 text-sm text-zinc-500 dark:text-zinc-500"
    >
      {items.map((item, index) => {
        const isLast = index === items.length - 1;

        return (
          <span key={`${item}-${index}`} className="flex min-w-0 items-center gap-1">
            <span
              className={[
                "truncate",
                isLast ? "text-zinc-700 dark:text-zinc-300" : "",
              ].join(" ")}
            >
              {item}
            </span>
            {!isLast && (
              <span className="shrink-0 text-zinc-400 dark:text-zinc-600">/</span>
            )}
          </span>
        );
      })}
    </nav>
  );
}

type SidebarCollapseIconProps = {
  direction: "left" | "right";
};

function SidebarCollapseIcon({ direction }: SidebarCollapseIconProps) {
  const arrowPath =
    direction === "left" ? "M17 8L13 12L17 16" : "M13 8L17 12L13 16";
  const arrowLine = direction === "left" ? "M13 12H22" : "M8 12H17";

  return (
    <svg
      className="size-5"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth="1.8"
      aria-hidden="true"
    >
      <path d="M7 5V19" />
      <path d={arrowLine} />
      <path d={arrowPath} />
    </svg>
  );
}

export function PageSection({ children }: { children: ReactNode }) {
  return <div className="space-y-6">{children}</div>;
}

export function SummaryPanel({
  icon,
  title,
  description,
  metric,
}: {
  icon: ReactNode;
  title: string;
  description: string;
  metric?: string;
}) {
  return (
    <div className="rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm dark:border-zinc-800 dark:bg-zinc-950/40 dark:shadow-none">
      <div className="flex items-center justify-between gap-4">
        <div className="flex min-w-0 items-start gap-3">
          <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-xl border border-zinc-200 bg-zinc-50 text-zinc-700 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-300">
            {icon}
          </span>
          <div className="min-w-0">
            <div className="text-sm font-semibold text-zinc-950 dark:text-zinc-100">
              {title}
            </div>
            <p className="mt-1 text-sm text-zinc-600 dark:text-zinc-400">
              {description}
            </p>
          </div>
        </div>
        {metric && (
          <div className="inline-flex shrink-0 items-center gap-2 rounded-full border border-zinc-200 bg-zinc-50 px-4 py-2 text-sm font-medium text-zinc-700 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-300">
            <span className="size-1.5 rounded-full bg-emerald-500" />
            {metric}
          </div>
        )}
      </div>
    </div>
  );
}

export function EmptyPanel({
  icon,
  title,
  description,
}: {
  icon: ReactNode;
  title: string;
  description: string;
}) {
  return (
    <div className="rounded-2xl border border-zinc-200 bg-white p-8 text-center shadow-sm dark:border-zinc-800 dark:bg-zinc-950/50 dark:shadow-none">
      <div className="mx-auto flex size-11 items-center justify-center rounded-2xl border border-zinc-200 bg-zinc-50 text-zinc-500 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-400">
        {icon}
      </div>
      <div className="mt-4 text-sm font-medium text-zinc-950 dark:text-zinc-100">
        {title}
      </div>
      <div className="mt-2 text-sm text-zinc-600 dark:text-zinc-500">
        {description}
      </div>
    </div>
  );
}
