import { Toaster as Sonner, type ToasterProps } from "sonner";

export function Toaster(props: ToasterProps) {
  return (
    <Sonner
      position="bottom-right"
      style={{ fontFamily: "inherit", overflowWrap: "anywhere" }}
      toastOptions={{
        unstyled: true,
        classNames: {
          toast:
            "bg-card text-card-foreground border-border border-2 font-semibold shadow-shadow rounded-base text-[13px] flex items-center gap-2.5 p-4 w-[356px] [&:has(button)]:justify-between",
          description: "font-normal text-muted-foreground",
          actionButton:
            "border-2 text-[12px] h-6 px-2 bg-main text-main-foreground border-border rounded-base shrink-0",
          cancelButton:
            "border-2 text-[12px] h-6 px-2 bg-secondary-background text-foreground border-border rounded-base shrink-0",
        },
      }}
      {...props}
    />
  );
}
