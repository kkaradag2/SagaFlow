export type SagaFlowMetadata = {
  name: string;
  status: string;
};

export async function getMetadata(): Promise<SagaFlowMetadata> {
  const response = await fetch("/sagaflow/metadata");

  if (!response.ok) {
    throw new Error("Unable to load SagaFlow metadata.");
  }

  return response.json() as Promise<SagaFlowMetadata>;
}
