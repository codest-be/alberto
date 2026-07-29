import * as React from "react";
import { useIsMobile } from "@/hooks/use-mobile";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
  DrawerTrigger,
} from "@/components/ui/drawer";

interface ResponsiveDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  children: React.ReactNode;
}

function ResponsiveDialog({
  open,
  onOpenChange,
  children,
}: ResponsiveDialogProps) {
  const isMobile = useIsMobile();

  if (isMobile) {
    return (
      <Drawer open={open} onOpenChange={onOpenChange}>
        {children}
      </Drawer>
    );
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      {children}
    </Dialog>
  );
}

function ResponsiveDialogTrigger({
  children,
  ...props
}: React.ComponentPropsWithoutRef<typeof DialogTrigger>) {
  const isMobile = useIsMobile();
  const Comp = isMobile ? DrawerTrigger : DialogTrigger;
  return <Comp {...props}>{children}</Comp>;
}

function ResponsiveDialogContent({
  className,
  children,
  ...props
}: React.ComponentPropsWithoutRef<typeof DialogContent>) {
  const isMobile = useIsMobile();

  if (isMobile) {
    return (
      <DrawerContent className={className}>
        <div className="overflow-y-auto px-4 pb-4">{children}</div>
      </DrawerContent>
    );
  }

  return (
    <DialogContent className={className} {...props}>
      {children}
    </DialogContent>
  );
}

function ResponsiveDialogHeader({
  className,
  ...props
}: React.HTMLAttributes<HTMLDivElement>) {
  const isMobile = useIsMobile();
  const Comp = isMobile ? DrawerHeader : DialogHeader;
  return <Comp className={className} {...props} />;
}

function ResponsiveDialogFooter({
  className,
  ...props
}: React.HTMLAttributes<HTMLDivElement>) {
  const isMobile = useIsMobile();
  const Comp = isMobile ? DrawerFooter : DialogFooter;
  return <Comp className={className} {...props} />;
}

function ResponsiveDialogTitle({
  className,
  ...props
}: React.ComponentPropsWithoutRef<typeof DialogTitle>) {
  const isMobile = useIsMobile();
  const Comp = isMobile ? DrawerTitle : DialogTitle;
  return <Comp className={className} {...props} />;
}

function ResponsiveDialogDescription({
  className,
  ...props
}: React.ComponentPropsWithoutRef<typeof DialogDescription>) {
  const isMobile = useIsMobile();
  const Comp = isMobile ? DrawerDescription : DialogDescription;
  return <Comp className={className} {...props} />;
}

function ResponsiveDialogClose({
  children,
  ...props
}: React.ComponentPropsWithoutRef<typeof DrawerClose>) {
  const isMobile = useIsMobile();

  if (isMobile) {
    return <DrawerClose {...props}>{children}</DrawerClose>;
  }

  return null;
}

export {
  ResponsiveDialog,
  ResponsiveDialogTrigger,
  ResponsiveDialogContent,
  ResponsiveDialogHeader,
  ResponsiveDialogFooter,
  ResponsiveDialogTitle,
  ResponsiveDialogDescription,
  ResponsiveDialogClose,
};
