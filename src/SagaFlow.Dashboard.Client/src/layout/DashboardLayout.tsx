import {
  Boxes,
  LayoutDashboard,
  Network,
  PanelLeftOpen,
  type LucideIcon,
} from "lucide-react";
import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";

import { SidebarItem } from "../components/Sidebar/SidebarItem";
import { ThemeToggle } from "../components/ThemeToggle/ThemeToggle";
import { BigPicturePage } from "../pages/BigPicturePage";
import { InstancesPage } from "../pages/InstancesPage";
import { OverviewPage } from "../pages/OverviewPage";
import { getDefinitions, type SagaDefinition, type InstanceContext } from "../api/sagaflowApi";
import { instanceContextFromSearch, instanceContextQuery, workflowFromPath, workflowPath, type WorkflowIdentity } from "./bigPictureRoute";

const pages = [
  "Overview",
  "Process Explorer",
  "Instances",
] as const;

type Page = (typeof pages)[number];

const navigationIcons: Record<Page, LucideIcon> = {
  Overview: LayoutDashboard,
  "Process Explorer": Network,
  Instances: Boxes,
};

const breadcrumbs: Record<Page, string[]> = {
  Overview: ["Dashboard", "Overview"],
  "Process Explorer": ["Dashboard", "Process Explorer"],
  Instances: ["Dashboard", "Saga Instances"],
};

const routeName: Record<Page, string> = { Overview: "overview", "Process Explorer": "bigpicture", Instances: "instances" };
function routeBase() {
  const path = window.location.pathname.replace(/\/$/, "");
  return path.replace(/\/(?:overview|workflows|bigpicture(?:\/workflow\/[^/]+\/[^/]+(?:\/[^/]+)?)?|instances)$/i, "") || "/sagaflow";
}
function pageFromPath(): Page {
  if (workflowFromPath(window.location.pathname)) return "Process Explorer";
  const segment = window.location.pathname.replace(/\/$/, "").split("/").pop()?.toLowerCase();
  return segment === "overview" ? "Overview" : segment === "instances" ? "Instances" : "Process Explorer";
}

export function DashboardLayout() {
  const [activePage, setActivePage] = useState<Page>(pageFromPath);
  const [activeWorkflow, setActiveWorkflow] = useState<WorkflowIdentity | null>(() => workflowFromPath(window.location.pathname));
  const [search, setSearch] = useState(() => window.location.search);
  const instanceContext = useMemo(() => instanceContextFromSearch(search), [search]);
  const instanceId = new URLSearchParams(search).get("instanceId");
  const [definitions, setDefinitions] = useState<SagaDefinition[]>([]);
  const [definitionsLoading, setDefinitionsLoading] = useState(true);
  const [definitionsError, setDefinitionsError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);
  const [isSidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [isMobileSidebarOpen, setMobileSidebarOpen] = useState(false);

  useEffect(() => {
    const abort = new AbortController();
    setDefinitionsLoading(true);
    setDefinitionsError(null);
    setDefinitions([]);
    void getDefinitions(abort.signal).then(result => { if (!abort.signal.aborted) setDefinitions(result); })
      .catch(failure => { if (!abort.signal.aborted) setDefinitionsError(failure instanceof Error ? failure.message : "Unable to load process definitions."); })
      .finally(() => { if (!abort.signal.aborted) setDefinitionsLoading(false); });
    return () => abort.abort();
  }, [reload]);

  useEffect(() => {
    const onPopState = () => { setActivePage(pageFromPath()); setActiveWorkflow(workflowFromPath(window.location.pathname)); setSearch(window.location.search); };
    window.addEventListener("popstate", onPopState);
    return () => window.removeEventListener("popstate", onPopState);
  }, []);

  const handleWorkflowOpen = useCallback(() => {
    setSidebarCollapsed(!window.matchMedia("(max-width: 1023px)").matches);
    setMobileSidebarOpen(false);
  }, []);

  const topologyHref = `${routeBase()}/bigpicture${instanceContextQuery(instanceContext)}`;
  const handleWorkflowBack = useCallback(() => {
    window.history.pushState(null, "", topologyHref);
    setActiveWorkflow(null);
  }, [topologyHref]);
  const workflowDomain = activePage === "Process Explorer" ? activeWorkflow?.domain : undefined;
  const breadcrumbItems: BreadcrumbItem[] = workflowDomain ? [
    "Dashboard",
    { label: "Process Explorer", href: topologyHref, navigate: handleWorkflowBack },
    workflowDomain.charAt(0).toUpperCase() + workflowDomain.slice(1),
  ] : breadcrumbs[activePage];

  const content = useMemo(() => {
    switch (activePage) {
      case "Overview":
        return <OverviewPage />;
      case "Process Explorer":
        if (definitionsLoading) return <div role="status" className="text-sm text-zinc-500">Loading process definitions…</div>;
        if (definitionsError) return <div className="space-y-3 text-sm">
          <p role="alert" className="text-red-600 dark:text-red-400">{definitionsError}</p>
          <button type="button" className="rounded-lg border px-3 py-2" onClick={() => setReload(value => value + 1)}>Retry loading definitions</button>
        </div>;
        return <BigPicturePage definitions={definitions} workflow={activeWorkflow} instanceContext={instanceContext}
            onLocalInstanceOpen={id => {
              const query = `?${new URLSearchParams({ instanceId: id })}`;
              window.history.pushState(null, "", `${routeBase()}/instances${query}`);
              setSearch(query); setActivePage("Instances"); setActiveWorkflow(null);
            }}
            onContextClear={() => { window.history.pushState(null, "", `${routeBase()}/bigpicture`); setSearch(""); setActiveWorkflow(null); }}
            onWorkflowOpen={workflow => {
              window.history.pushState(null, "", workflowPath(routeBase(), workflow) + instanceContextQuery(instanceContext));
              setActiveWorkflow(workflow);
              handleWorkflowOpen();
            }} onWorkflowBack={handleWorkflowBack} />;
      case "Instances":
        return <InstancesPage key={instanceId ?? "list"} initialInstanceId={instanceId}
          onInstanceNavigate={id => {
            const query = id ? `?${new URLSearchParams({ instanceId: id })}` : "";
            window.history.pushState(null, "", `${routeBase()}/instances${query}`); setSearch(query);
          }} onBigPictureOpen={(context: InstanceContext) => {
            const query = instanceContextQuery(context);
            window.history.pushState(null, "", `${routeBase()}/bigpicture${query}`);
            setSearch(query); setActivePage("Process Explorer"); setActiveWorkflow(null);
          }} />;
    }
  }, [activePage, activeWorkflow, handleWorkflowOpen, handleWorkflowBack, definitions, definitionsLoading, definitionsError, instanceContext, instanceId]);

  const closeMobileSidebar = () => {
    setMobileSidebarOpen(false);
  };

  const navigateTo = (page: Page) => {
    window.history.pushState(null, "", `${routeBase()}/${routeName[page]}`);
    setActivePage(page);
    setActiveWorkflow(null);
    setSearch("");
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
          onClick={() => navigateTo("Process Explorer")}
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

              <Breadcrumb items={breadcrumbItems} />
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

type BreadcrumbItem = string | { label: string; href: string; navigate: () => void };

function Breadcrumb({ items }: { items: BreadcrumbItem[] }) {
  return (
    <nav
      aria-label="Breadcrumb"
      className="mt-1 flex h-5 min-w-0 items-center gap-1 text-sm text-zinc-500 dark:text-zinc-500"
    >
      {items.map((item, index) => {
        const isLast = index === items.length - 1;
        const label = typeof item === "string" ? item : item.label;

        return (
          <span key={`${label}-${index}`} className="flex min-w-0 items-center gap-1">
            {typeof item === "string" ? (
              <span aria-current={isLast ? "page" : undefined}
                className={`truncate ${isLast ? "text-zinc-700 dark:text-zinc-300" : ""}`}>
                {label}
              </span>
            ) : (
              <a href={item.href} onClick={event => {
                if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                event.preventDefault();
                item.navigate();
              }} className="truncate rounded-sm hover:underline focus-visible:outline focus-visible:outline-emerald-500">
                {label}
              </a>
            )}
            {!isLast && (
              <span aria-hidden="true" className="shrink-0 text-zinc-400 dark:text-zinc-600">/</span>
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
