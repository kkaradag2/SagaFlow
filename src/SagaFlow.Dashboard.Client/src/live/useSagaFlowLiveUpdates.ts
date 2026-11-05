import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { useEffect, useRef } from "react";

const eventName = "SagaInstanceChanged";

export function useSagaFlowLiveUpdates(onInstanceChanged: (instanceId: string | null) => void | Promise<void>) {
  const callback = useRef(onInstanceChanged);

  useEffect(() => { callback.current = onInstanceChanged; }, [onInstanceChanged]);

  useEffect(() => {
    let disposed = false;
    let initialRetry: number | undefined;
    const connection = new HubConnectionBuilder()
      .withUrl(getHubPath())
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();

    const handleChange = (instanceId: string) => {
      if (typeof instanceId === "string" && instanceId.length > 0) void callback.current(instanceId);
    };
    connection.on(eventName, handleChange);
    connection.onreconnected(() => void callback.current(null));

    const start = async () => {
      if (disposed || connection.state !== HubConnectionState.Disconnected) return;
      try { await connection.start(); }
      catch {
        if (!disposed) initialRetry = window.setTimeout(() => void start(), 2000);
      }
    };

    void start();
    return () => {
      disposed = true;
      if (initialRetry !== undefined) window.clearTimeout(initialRetry);
      connection.off(eventName, handleChange);
      void connection.stop();
    };
  }, []);
}

export function getHubPath() {
  // The embedded middleware supplies a base URL for nested Dashboard routes.
  // Resolve exactly like the API client rather than appending hub to /instances or a workflow URL.
  return new URL("hub", document.baseURI).pathname;
}
